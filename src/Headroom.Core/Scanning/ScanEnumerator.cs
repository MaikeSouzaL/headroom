using System.IO.Enumeration;

namespace Headroom.Core.Scanning;

/// <summary>
/// Enumerador que monta a árvore enquanto lê o disco, baseado em
/// <see cref="FileSystemEnumerator{TResult}"/> (NtQueryDirectoryFile com buffer de 64 KB, sem
/// alocar FileInfo). No modo recursivo, o .NET abre cada subpasta por handle relativo à pasta-pai,
/// o que é cerca de 2× mais rápido do que abrir pelo caminho completo. Se outras threads ficam
/// ociosas, as próximas subpastas encontradas são doadas para a fila de trabalho em vez de serem
/// percorridas aqui (balanceamento dinâmico). No modo raso, só a pasta atual é lida.
/// </summary>
internal sealed class ScanEnumerator : FileSystemEnumerator<byte>
{
    /// <summary>
    /// Equivale a IgnoreInaccessible = true, mas o erro é tratado em <see cref="ContinueOnError"/>
    /// para contar as pastas sem permissão. AttributesToSkip = 0 porque arquivos do OneDrive
    /// disponíveis localmente também são reparse points e precisam ser contados; junctions e links
    /// simbólicos de pasta são recusados em <see cref="ShouldRecurseIntoEntry"/> (nunca seguidos).
    /// </summary>
    private static EnumerationOptions CreateOptions(bool recursive) => new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = recursive,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private static readonly EnumerationOptions RecursiveOptions = CreateOptions(true);
    private static readonly EnumerationOptions ShallowOptions = CreateOptions(false);

    // O construtor base já abre a pasta e pode chamar ContinueOnError antes de os campos desta
    // classe serem atribuídos; por isso o contexto também vai por campos [ThreadStatic].
    [ThreadStatic] private static ScanContext? t_pendingContext;
    [ThreadStatic] private static string? t_pendingPath;

    private readonly ScanContext? _context;
    private readonly NamePool? _names;
    private readonly bool _recursive;
    private readonly Dictionary<string, (DirectoryNode Node, int Depth)>? _pending;
    private string? _currentPath;
    private DirectoryNode _currentNode;
    private int _currentDepth;
    private long _files;
    private long _bytes;
    private long _directories;
    private string? _lastRecursedName;
    private string? _donatedName;

    private ScanEnumerator(DirectoryNode node, string path, int depth, bool recursive, ScanContext context)
        : base(path, recursive ? RecursiveOptions : ShallowOptions)
    {
        _context = context;
        _names = context.Names;
        _recursive = recursive;
        _currentNode = node;
        _currentDepth = depth;
        // Mesma normalização que o FileSystemEnumerator usa para FileSystemEntry.Directory.
        _currentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        _pending = recursive ? new Dictionary<string, (DirectoryNode, int)>(StringComparer.Ordinal) : null;
    }

    private ScanContext Context => _context!;

    /// <summary>Lê a pasta (e, se recursivo, a subárvore) preenchendo os nós.</summary>
    public static void Run(DirectoryNode node, string path, int depth, bool recursive, ScanContext context)
    {
        t_pendingContext = context;
        t_pendingPath = path;
        ScanEnumerator enumerator;
        try
        {
            enumerator = new ScanEnumerator(node, path, depth, recursive, context);
        }
        finally
        {
            t_pendingContext = null;
            t_pendingPath = null;
        }

        using (enumerator)
        {
            context.CurrentPath = path;
            var token = context.Token;
            while (!token.IsCancellationRequested && enumerator.MoveNext())
            {
            }

            enumerator.Flush();
        }
    }

    protected override bool ShouldIncludeEntry(ref FileSystemEntry entry) => true;

    protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || Context.IsExcluded(ref entry))
        {
            return false;
        }

        // Guardado para (1) reaproveitar a string no nó e (2) saber qual subpasta falhou ao abrir.
        _lastRecursedName = _names!.Get(entry.FileName);

        if (Context.WantsWork)
        {
            // Há threads ociosas: esta subpasta vai para a fila em vez de ser percorrida aqui.
            _donatedName = _lastRecursedName;
            return false;
        }

        return true;
    }

    protected override byte TransformEntry(ref FileSystemEntry entry)
    {
        var directory = entry.Directory;
        if (_recursive && !directory.SequenceEqual(_currentPath))
        {
            SwitchDirectory(directory);
        }

        if (entry.IsDirectory)
        {
            // Junctions e links simbólicos: nunca seguir (evita contar duas vezes e laços).
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Context.AddLinkSkipped();
                return 0;
            }

            if (Context.IsExcluded(ref entry))
            {
                return 0;
            }

            string dirName = _lastRecursedName is { } last && entry.FileName.SequenceEqual(last)
                ? last
                : _names!.Get(entry.FileName);
            bool donated = _donatedName is not null && ReferenceEquals(_donatedName, dirName);
            _lastRecursedName = null;
            _donatedName = null;

            var child = ScanNode.CreateDirectory(dirName, _currentNode, entry.LastWriteTimeUtc.UtcTicks);
            _currentNode.AddChild(child);
            _directories++;
            string childPath = Path.Join(directory, entry.FileName);
            if (_recursive && !donated)
            {
                _pending![childPath] = (child, _currentDepth + 1);
            }
            else
            {
                Context.Enqueue(child, childPath, _currentDepth + 1);
            }

            return 0;
        }

        if (Context.SkipCloudOnly && DiskScanner.IsCloudOnly(entry.Attributes))
        {
            Context.AddCloudSkipped();
            return 0;
        }

        var fileName = entry.FileName;
        string name = _names!.Get(fileName);
        var extensionSpan = CategoryClassifier.GetExtensionSpan(fileName);
        string extension = extensionSpan.IsEmpty ? string.Empty : _names.Get(extensionSpan);
        var category = CategoryClassifier.ClassifyExtension(extension);
        long length = entry.Length;
        var file = ScanNode.CreateFile(name, _currentNode, length, entry.LastWriteTimeUtc.UtcTicks, category);
        _currentNode.AddChild(file);
        Context.Statistics.Add(extension, category, length);
        Context.Largest.Offer(file);
        _files++;
        _bytes += length;
        return 0;
    }

    private void SwitchDirectory(ReadOnlySpan<char> directory)
    {
        Flush();
        string key = directory.ToString();
        if (_pending!.Remove(key, out var pending))
        {
            _currentNode = pending.Node;
            _currentDepth = pending.Depth;
        }

        _currentPath = key;
        Context.CurrentPath = key;
    }

    private void Flush()
    {
        Context.AddCounts(_files, _bytes, _directories);
        _files = 0;
        _bytes = 0;
        _directories = 0;
    }

    protected override bool ContinueOnError(int error)
    {
        var context = _context ?? t_pendingContext;
        string path = _currentPath ?? t_pendingPath ?? string.Empty;
        if (_lastRecursedName is not null)
        {
            // A falha foi ao abrir a subpasta que acabou de ser aceita para recursão.
            path = Path.Join(path, _lastRecursedName);
        }

        context?.RegisterError(path, error);
        return true;
    }
}
