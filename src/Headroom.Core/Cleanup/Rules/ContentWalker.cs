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
/// <para>
/// Em caches com centenas de milhares de pastas (Gradle, uv, Cargo, npm), o custo é abrir e listar cada
/// pasta no kernel; em uma thread só isso levava muitos minutos. Aqui cada pasta é listada uma vez por
/// qualquer thread livre e o resultado de cada subárvore é consolidado de baixo para cima assim que todas as
/// subpastas terminam. Os itens só são montados no fim, e só para as pastas que não puderam ser agrupadas.
/// </para>
/// <para>
/// Hardlinks: em NTFS local a pasta é listada com <c>FileIdBothDirectoryInfo</c>, que traz o file ID de cada
/// arquivo sem abrir o arquivo. Um arquivo com vários links (uv, pnpm, conda) soma o tamanho uma vez só por
/// análise (<see cref="HardLinkSet"/>); os outros caminhos dele entram com tamanho zero. Em outros sistemas de
/// arquivos (FAT, exFAT, ReFS, rede) a listagem é a do .NET, sem deduplicação.
/// </para>
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
    private readonly HardLinkSet _links;
    private readonly string? _skip;
    private readonly string? _skipNamed;
    private readonly FileSystemEnumerable<Entry>.FindTransform _transform;
    private readonly DirectoryReader.Transform<Entry> _nativeTransform;

    private readonly object _lock = new();
    private readonly Stack<Node> _work = new();
    private int _threads = 1;
    private bool _done;
    private ExceptionDispatchInfo? _error;

    /// <summary>Serial do volume NTFS local (listagem com file ID e deduplicação); null = listagem do .NET.</summary>
    private uint? _volume;

    private ContentWalker(PathTarget? target, CancellationToken ct, int maxThreads, HardLinkSet? links, string? skip = null,
        string? skipNamed = null)
    {
        _target = target;
        _ct = ct;
        _maxThreads = Math.Max(1, maxThreads);
        _links = links ?? new HardLinkSet();
        _skip = skip is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(skip));
        _skipNamed = skipNamed;
        if (target is not null)
        {
            _cutoff = DateTime.Now.AddDays(-target.MinAgeDays);
            _extensions = target.Extensions is { Count: > 0 } exts
                ? exts.Select(e => e.StartsWith('.') ? e : "." + e).ToArray()
                : null;
        }

        _transform = Transform;
        _nativeTransform = NativeTransform;
    }

    /// <summary>Resultado de <see cref="Collect"/>.</summary>
    internal readonly record struct ContentResult(bool AllMatch, long Size, int Files, DateTime Newest, List<CleanupItem> Items);

    /// <summary>
    /// Coleta os itens de uma pasta. Se TUDO dentro de uma subpasta passa nos filtros, a subpasta inteira
    /// vira um único item; senão, só os arquivos que passam são listados.
    /// </summary>
    /// <param name="links">Arquivos já somados nesta análise (vários percursos da mesma regra); null = só este percurso.</param>
    /// <param name="skipDirectory">Subpasta que a regra trata à parte: não é percorrida nem vira item (como uma excluída).</param>
    public static ContentResult Collect(string directory, PathTarget target, CancellationToken ct, int? maxThreads = null,
        HardLinkSet? links = null, string? skipDirectory = null)
    {
        var walker = new ContentWalker(target, ct, maxThreads ?? ThreadsFor(directory), links, skipDirectory);
        var root = walker.Run(directory);
        var items = new List<CleanupItem>();
        Emit(root, items);
        return new ContentResult(root.AllMatch, root.Size, root.Files, root.Newest, items);
    }

    /// <summary>Tamanho, arquivos e data mais recente de uma pasta (sem seguir links; hardlinks contam uma vez).</summary>
    /// <param name="skipDirectoriesNamed">
    /// Curinga de subpastas que não são abertas (só para estimativas, quando se sabe que vêm vazias; ex.: as sobras
    /// <c>.tmp*</c> que o uv deixa dentro dos ambientes). Não afeta o que é apagado.
    /// </param>
    public static (long Size, int Files, DateTime Newest) Measure(string directory, CancellationToken ct, int? maxThreads = null,
        HardLinkSet? links = null, string? skipDirectoriesNamed = null)
    {
        var walker = new ContentWalker(null, ct, maxThreads ?? ThreadsFor(directory), links, skipNamed: skipDirectoriesNamed);
        var root = walker.Run(directory);
        return (root.Size, root.Files, root.Newest);
    }

    /// <summary>Em HD mecânico várias threads fazem a cabeça de leitura saltar; duas bastam (como no scan).</summary>
    internal static int ThreadsFor(string directory)
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
        _volume = DirectoryReader.LocalNtfsVolume(directory);
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
            // Buffer da listagem nativa, um por thread (fixo na memória: vai direto para o kernel).
            byte[]? buffer = _volume is null ? null : DirectoryReader.NewBuffer();
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

                Process(node, buffer);
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
    private void Process(Node node, byte[]? buffer)
    {
        _ct.ThrowIfCancellationRequested();

        // Mesma normalização do FileSystemEnumerator: é a pasta que aparece no FullName de cada entrada.
        node.ListedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(node.Path));

        Entry[] entries;
        try
        {
            entries = buffer is null ? ListManaged(node.ListedPath) : ListNative(node.ListedPath, buffer);
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

            if (_skipNamed is not null && PathExpander.MatchesWildcard(e.Name!, _skipNamed))
            {
                continue;
            }

            string childPath = Path.Join(node.ListedPath, e.Name);
            if (_skip is not null && string.Equals(childPath, _skip, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            e.Child = new Node(childPath, node);
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

            if (e.FileId != 0 && !_links.TryAdd(_volume!.Value, e.FileId))
            {
                // Outro hardlink do mesmo arquivo já foi somado: apagar este caminho não libera espaço de novo.
                e.Length = 0;
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

    private Entry[] ListManaged(string directory)
    {
        var list = new List<Entry>();
        foreach (var entry in new FileSystemEnumerable<Entry>(directory, _transform, ShallowOptions))
        {
            _ct.ThrowIfCancellationRequested();
            list.Add(entry);
        }

        return list.ToArray();
    }

    private Entry Transform(ref FileSystemEntry entry) =>
        // Igual a FileSystemInfo.LastWriteTime (mesma origem e mesma conversão para hora local).
        MakeEntry(entry.FileName, entry.Attributes, entry.IsDirectory ? 0 : entry.Length,
            entry.LastWriteTimeUtc.UtcDateTime.ToLocalTime(), fileId: 0);

    /// <summary>
    /// Lista a pasta com o file ID de cada arquivo (<see cref="DirectoryReader"/>): as mesmas entradas, na mesma ordem
    /// e com os mesmos dados do FileSystemEnumerable. Falha ao abrir conta como pasta vazia, como no .NET com
    /// IgnoreInaccessible; erro no meio da leitura vira IOException (a pasta conta como vazia em <see cref="Process"/>).
    /// </summary>
    private Entry[] ListNative(string directory, byte[] buffer)
    {
        var list = new List<Entry>();
        return DirectoryReader.TryList(directory, buffer, _nativeTransform, list, _ct, out _) ? list.ToArray() : Array.Empty<Entry>();
    }

    private Entry NativeTransform(ReadOnlySpan<char> name, FileAttributes attributes, long length, long lastWriteTimeUtc, long fileId)
    {
        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        return MakeEntry(name, attributes, isDirectory ? 0 : length, DateTime.FromFileTimeUtc(lastWriteTimeUtc).ToLocalTime(),
            isDirectory ? 0 : fileId);
    }

    private Entry MakeEntry(ReadOnlySpan<char> name, FileAttributes attributes, long length, DateTime lastWrite, long fileId)
    {
        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        var result = new Entry
        {
            IsDirectory = isDirectory,
            Attributes = attributes,
            Length = length,
            LastWrite = lastWrite,
            FileId = fileId,
        };

        if (isDirectory)
        {
            result.Name = name.ToString();
        }
        else if (_target is null)
        {
            result.Matches = true; // medir: todo arquivo que não é link conta
        }
        else
        {
            result.Name = name.ToString();
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

        /// <summary>Tamanho do arquivo; vira zero quando outro hardlink do mesmo arquivo já foi somado.</summary>
        public long Length;

        public DateTime LastWrite;
        public FileAttributes Attributes;
        public bool IsDirectory;
        public bool Matches;

        /// <summary>File ID do NTFS (0 = desconhecido: sem deduplicação).</summary>
        public long FileId;

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
