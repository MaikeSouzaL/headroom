using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.ExceptionServices;

namespace Headroom.Core.Cleanup.Rules;

/// <summary>
/// Percorre a pasta de um alvo <see cref="TargetMode.Contents"/> (ou só mede uma pasta) com várias threads
/// e sem criar FileInfo/DirectoryInfo por arquivo. O resultado é o mesmo do percurso recursivo sequencial
/// que existia antes: mesmos itens, na mesma ordem, mesmos filtros, pastas inteiras agrupadas quando tudo
/// dentro delas pode sair, e links/junctions nunca são seguidos nem incluídos.
/// </summary>
/// <remarks>
/// Em caches com centenas de milhares de pastas (Gradle, uv, Cargo, npm), o custo é abrir e listar cada
/// pasta no kernel; em uma thread só isso levava muitos minutos. Aqui cada pasta é listada uma vez por
/// qualquer thread livre e o resultado de cada subárvore é consolidado de baixo para cima assim que todas as
/// subpastas terminam. Os itens só são montados no fim, e só para as pastas que não puderam ser agrupadas.
/// </remarks>
internal sealed class ContentWalker
{
    /// <summary>Threads por percurso (a análise roda até 4 regras ao mesmo tempo).</summary>
    internal static readonly int DefaultMaxThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    private static readonly EnumerationOptions ShallowOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private static readonly ConcurrentDictionary<string, bool> SeekPenaltyByRoot = new(StringComparer.OrdinalIgnoreCase);

    private readonly PathTarget? _target;
    private readonly DateTime _cutoff;
    private readonly string[]? _extensions;
    private readonly CancellationToken _ct;
    private readonly int _maxThreads;
    private readonly FileSystemEnumerable<Entry>.FindTransform _transform;

    private readonly object _lock = new();
    private readonly Stack<Node> _work = new();
    private int _threads = 1;
    private bool _done;
    private ExceptionDispatchInfo? _error;

    private ContentWalker(PathTarget? target, CancellationToken ct, int maxThreads)
    {
        _target = target;
        _ct = ct;
        _maxThreads = Math.Max(1, maxThreads);
        if (target is not null)
        {
            _cutoff = DateTime.Now.AddDays(-target.MinAgeDays);
            _extensions = target.Extensions is { Count: > 0 } exts
                ? exts.Select(e => e.StartsWith('.') ? e : "." + e).ToArray()
                : null;
        }

        _transform = Transform;
    }

    /// <summary>Resultado de <see cref="Collect"/>.</summary>
    internal readonly record struct ContentResult(bool AllMatch, long Size, int Files, DateTime Newest, List<CleanupItem> Items);

    /// <summary>
    /// Coleta os itens de uma pasta. Se TUDO dentro de uma subpasta passa nos filtros, a subpasta inteira
    /// vira um único item; senão, só os arquivos que passam são listados.
    /// </summary>
    public static ContentResult Collect(string directory, PathTarget target, CancellationToken ct, int? maxThreads = null)
    {
        var walker = new ContentWalker(target, ct, maxThreads ?? MaxThreadsFor(directory));
        var root = walker.Run(directory);
        var items = new List<CleanupItem>();
        Emit(root, items);
        return new ContentResult(root.AllMatch, root.Size, root.Files, root.Newest, items);
    }

    /// <summary>Tamanho, arquivos e data mais recente de uma pasta (sem seguir links).</summary>
    public static (long Size, int Files, DateTime Newest) Measure(string directory, CancellationToken ct, int? maxThreads = null)
    {
        var walker = new ContentWalker(null, ct, maxThreads ?? MaxThreadsFor(directory));
        var root = walker.Run(directory);
        return (root.Size, root.Files, root.Newest);
    }

    /// <summary>Em HD mecânico várias threads fazem a cabeça de leitura saltar; duas bastam (como no scan).</summary>
    private static int MaxThreadsFor(string directory)
    {
        string? root = null;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        if (string.IsNullOrEmpty(root))
        {
            return DefaultMaxThreads;
        }

        bool hdd = SeekPenaltyByRoot.GetOrAdd(root, r => Drives.DriveService.HasSeekPenalty(r) == true);
        return hdd ? Math.Min(DefaultMaxThreads, 2) : DefaultMaxThreads;
    }

    private Node Run(string directory)
    {
        var root = new Node(directory, null);
        _work.Push(root);
        WorkerLoop();
        _error?.Throw();
        _ct.ThrowIfCancellationRequested();
        return root;
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                Node node;
                lock (_lock)
                {
                    while (_work.Count == 0 && !_done)
                    {
                        Monitor.Wait(_lock);
                    }

                    if (_done)
                    {
                        return;
                    }

                    node = _work.Pop();
                }

                Process(node);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _error ??= ExceptionDispatchInfo.Capture(ex);
                _done = true;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>Lista uma pasta, agenda as subpastas que serão percorridas e consolida o que já terminou.</summary>
    private void Process(Node node)
    {
        _ct.ThrowIfCancellationRequested();

        // Mesma normalização do FileSystemEnumerator: é a pasta que aparece no FullName de cada entrada.
        node.ListedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(node.Path));

        Entry[] entries;
        try
        {
            var list = new List<Entry>();
            foreach (var entry in new FileSystemEnumerable<Entry>(node.ListedPath, _transform, ShallowOptions))
            {
                _ct.ThrowIfCancellationRequested();
                list.Add(entry);
            }

            entries = list.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pasta que sumiu ou não pôde ser lida até o fim: conta como vazia (comportamento anterior).
            entries = Array.Empty<Entry>();
        }

        List<Node>? children = null;
        for (int i = 0; i < entries.Length; i++)
        {
            ref var e = ref entries[i];
            if (!e.IsDirectory || (e.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue; // links/junctions nunca são seguidos
            }

            if (_target is not null && (!_target.Recursive || PathRule.IsExcluded(e.Name!, _target)))
            {
                continue;
            }

            e.Child = new Node(Path.Join(node.ListedPath, e.Name), node);
            (children ??= new List<Node>()).Add(e.Child);
        }

        node.Entries = entries;
        node.Pending = (children?.Count ?? 0) + 1;

        if (children is not null)
        {
            bool spawn;
            lock (_lock)
            {
                // Ordem inversa: a primeira subpasta sai primeiro da pilha (percurso em profundidade).
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    _work.Push(children[i]);
                }

                Monitor.PulseAll(_lock);
                spawn = _threads < _maxThreads && _work.Count > 1 && !_done;
                if (spawn)
                {
                    _threads++;
                }
            }

            if (spawn)
            {
                StartHelper();
            }
        }

        Complete(node);
    }

    private void StartHelper()
    {
        try
        {
            new Thread(WorkerLoop) { IsBackground = true, Name = "Headroom.ContentWalker" }.Start();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
        {
            lock (_lock)
            {
                _threads--;
            }
        }
    }

    /// <summary>Marca uma etapa de <paramref name="node"/> como concluída e consolida as pastas que ficaram completas.</summary>
    private void Complete(Node node)
    {
        while (Interlocked.Decrement(ref node.Pending) == 0)
        {
            Finish(node);
            if (node.Parent is null)
            {
                lock (_lock)
                {
                    _done = true;
                    Monitor.PulseAll(_lock);
                }

                return;
            }

            node = node.Parent;
        }
    }

    /// <summary>Mesma regra do percurso anterior, aplicada quando a pasta e todas as subpastas terminaram.</summary>
    private void Finish(Node node)
    {
        var entries = node.Entries!;
        bool allMatch = true;
        long size = 0;
        int files = 0;
        DateTime newest = DateTime.MinValue;
        bool requireAge = _target is { MinAgeDays: > 0 };

        for (int i = 0; i < entries.Length; i++)
        {
            ref var e = ref entries[i];
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                allMatch = false; // links nunca são incluídos nem seguidos
                continue;
            }

            if (e.IsDirectory)
            {
                if (e.Child is not { } sub)
                {
                    allMatch = false; // não recursivo ou excluída pelo nome
                    continue;
                }

                if (sub.Files == 0 && requireAge && e.LastWrite > _cutoff)
                {
                    // Pasta vazia criada há pouco (pode estar em uso por um instalador): fica.
                    allMatch = false;
                    e.Child = null;
                    continue;
                }

                if (!sub.AllMatch)
                {
                    allMatch = false;
                }

                size += sub.Size;
                files += sub.Files;
                if (sub.Newest > newest)
                {
                    newest = sub.Newest;
                }

                continue;
            }

            if (!e.Matches)
            {
                allMatch = false;
                continue;
            }

            size += e.Length;
            files++;
            if (e.LastWrite > newest)
            {
                newest = e.LastWrite;
            }
        }

        node.AllMatch = allMatch;
        node.Size = size;
        node.Files = files;
        node.Newest = newest;

        // Só a raiz e as pastas que não puderam ser agrupadas precisam das entradas para montar os itens.
        if (_target is null || (allMatch && node.Parent is not null))
        {
            node.Entries = null;
        }
    }

    /// <summary>Monta os itens na mesma ordem do percurso recursivo (antes da ordenação por tamanho).</summary>
    private static void Emit(Node node, List<CleanupItem> items)
    {
        var entries = node.Entries!;
        for (int i = 0; i < entries.Length; i++)
        {
            ref var e = ref entries[i];
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if (e.IsDirectory)
            {
                if (e.Child is not { } sub)
                {
                    continue;
                }

                if (sub.AllMatch)
                {
                    items.Add(new CleanupItem(sub.Path, sub.Size, true, sub.Newest == DateTime.MinValue ? e.LastWrite : sub.Newest, sub.Files));
                }
                else
                {
                    Emit(sub, items);
                }

                continue;
            }

            if (e.Matches)
            {
                items.Add(new CleanupItem(Path.Join(node.ListedPath, e.Name), e.Length, false, e.LastWrite));
            }
        }
    }

    private Entry Transform(ref FileSystemEntry entry)
    {
        bool isDirectory = entry.IsDirectory;
        var result = new Entry
        {
            IsDirectory = isDirectory,
            Attributes = entry.Attributes,
            Length = isDirectory ? 0 : entry.Length,
            // Igual a FileSystemInfo.LastWriteTime (mesma origem e mesma conversão para hora local).
            LastWrite = entry.LastWriteTimeUtc.UtcDateTime.ToLocalTime(),
        };

        if (isDirectory)
        {
            result.Name = entry.FileName.ToString();
        }
        else if (_target is null)
        {
            result.Matches = true; // medir: todo arquivo que não é link conta
        }
        else
        {
            result.Name = entry.FileName.ToString();
            result.Matches = FileMatches(result.Name, result.Length, result.LastWrite);
        }

        return result;
    }

    private bool FileMatches(string name, long length, DateTime lastWrite)
    {
        var target = _target!;
        if (!PathExpander.MatchesWildcard(name, target.Pattern) || PathRule.IsExcluded(name, target))
        {
            return false;
        }

        if (_extensions is not null && !_extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (target.MinAgeDays > 0 && lastWrite > _cutoff)
        {
            return false;
        }

        return length >= target.MinSize;
    }

    /// <summary>Uma entrada de pasta, na ordem devolvida pelo sistema.</summary>
    private struct Entry
    {
        public string? Name;
        public long Length;
        public DateTime LastWrite;
        public FileAttributes Attributes;
        public bool IsDirectory;
        public bool Matches;

        /// <summary>Subpasta percorrida (null = não entra: link, não recursivo, excluída ou vazia recente).</summary>
        public Node? Child;
    }

    private sealed class Node
    {
        public Node(string path, Node? parent)
        {
            Path = path;
            Parent = parent;
            ListedPath = path;
        }

        /// <summary>Caminho como o DirectoryInfo da pasta-pai o mostraria (usado no item da pasta).</summary>
        public string Path { get; }

        /// <summary>Caminho normalizado usado para listar e para montar o caminho dos arquivos.</summary>
        public string ListedPath { get; set; }

        public Node? Parent { get; }
        public Entry[]? Entries;
        public int Pending;
        public bool AllMatch;
        public long Size;
        public int Files;
        public DateTime Newest;
    }
}
