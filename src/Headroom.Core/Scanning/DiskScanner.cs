using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Principal;

namespace Headroom.Core.Scanning;

/// <summary>
/// Scanner de disco/pasta. Monta a árvore em memória, mantém os N maiores arquivos, agrega por
/// extensão/categoria e detecta artefatos de desenvolvimento. Roda em background, reporta
/// progresso via <see cref="IProgress{T}"/> e respeita <see cref="CancellationToken"/>.
/// </summary>
/// <remarks>
/// Paralelismo: começa pelas subpastas de primeiro nível e subdivide dinamicamente. Cada thread
/// pega uma pasta da fila; se a fila está curta (ou a pasta é rasa), lê só aquele nível e devolve
/// as subpastas para a fila, para outras threads pegarem; caso contrário, varre a subárvore inteira
/// com o enumerador recursivo do .NET, que abre subpastas por handle relativo (bem mais rápido).
/// Assim, pastas gigantes como C:\Windows\WinSxS ou AppData não ficam presas em uma única thread.
/// </remarks>
public sealed class DiskScanner
{
    // Atributos de arquivos que existem só na nuvem (OneDrive "Arquivos sob demanda" etc.).
    private const FileAttributes CloudOnlyMask = FileAttributes.Offline
        | (FileAttributes)0x00040000  // FILE_ATTRIBUTE_RECALL_ON_OPEN
        | (FileAttributes)0x00400000; // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

    public static bool IsCloudOnly(FileAttributes attributes) => (attributes & CloudOnlyMask) != 0;

    public Task<ScanResult> ScanAsync(
        string rootPath,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        options ??= new ScanOptions();

        string root = NormalizeRoot(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"A pasta \"{root}\" não existe ou não está acessível.");
        }

        return Task.Run(() => ScanCoreAsync(root, options, progress, cancellationToken), cancellationToken);
    }

    /// <summary>Normaliza o caminho: "c:" → "C:\", remove a barra final de pastas comuns.</summary>
    public static string NormalizeRoot(string path)
    {
        string trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsLetter(trimmed[0]))
        {
            // "C:" sozinho seria interpretado como "pasta atual do disco C:".
            trimmed += "\\";
        }

        string full = Path.GetFullPath(trimmed);
        string? pathRoot = Path.GetPathRoot(full);
        if (pathRoot is not null && full.TrimEnd('\\', '/').Length <= pathRoot.TrimEnd('\\', '/').Length)
        {
            return pathRoot.Length == 2 ? pathRoot.ToUpperInvariant() + "\\" : pathRoot.ToUpperInvariant();
        }

        return Path.TrimEndingDirectorySeparator(full);
    }

    private static async Task<ScanResult> ScanCoreAsync(
        string rootPath, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var startedAt = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();
        var context = new ScanContext(options, ct);

        DriveSnapshot? drive = TryGetDriveSnapshot(rootPath);
        var rootNode = (DirectoryNode)ScanNode.CreateRoot(rootPath, SafeLastWriteUtc(rootPath));

        using var timer = progress is null
            ? null
            : new Timer(_ => progress.Report(context.Snapshot(stopwatch.Elapsed)), null,
                options.ProgressInterval, options.ProgressInterval);

        context.AddCounts(0, 0, 1);
        context.Enqueue(rootNode, rootPath, depth: 0);
        int workerCount = Math.Max(1, options.MaxDegreeOfParallelism);

        // Em HD mecânico, muitas threads fazem a cabeça de leitura saltar entre pastas distantes e o scan
        // fica dezenas de vezes mais lento; duas threads mantêm o disco ocupado sem esse vaivém.
        if (options.LimitParallelismOnHdd && Drives.DriveService.HasSeekPenalty(rootPath) == true)
        {
            workerCount = Math.Min(workerCount, 2);
        }

        var workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            // Threads dedicadas: o trabalho é bloqueante (E/S de diretório) e não deve disputar o pool.
            workers[i] = Task.Factory.StartNew(context.WorkerLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // Agrega tamanhos de baixo para cima (cada subárvore de primeiro nível em paralelo).
        SealTree(rootNode, workerCount, ct);
        var artifacts = DevArtifactDetector.FindAll(rootNode).OrderByDescending(a => a.Size).ToList();

        stopwatch.Stop();
        progress?.Report(context.Snapshot(stopwatch.Elapsed) with { CurrentPath = rootPath });

        var (extensions, categoryBytes, categoryCounts) = context.MergeStatistics();
        return new ScanResult(
            rootPath,
            rootNode,
            startedAt,
            stopwatch.Elapsed,
            context.Directories,
            context.Inaccessible,
            context.InaccessibleSamples.ToArray(),
            context.CloudOnlySkipped,
            context.LinksSkipped,
            context.Largest.ToSortedList(),
            options.TopFilesCount,
            extensions,
            categoryBytes,
            categoryCounts,
            artifacts,
            drive,
            IsElevated());
    }

    /// <summary>Pós-ordem iterativa: cada pasta é finalizada depois de todas as subpastas.</summary>
    internal static void SealTree(DirectoryNode root, int parallelism, CancellationToken ct)
    {
        var topLevel = root.Children.OfType<DirectoryNode>().ToList();
        Parallel.ForEach(topLevel,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            SealSubtree);
        root.Seal();
    }

    private static void SealSubtree(DirectoryNode subtreeRoot)
    {
        var stack = new Stack<(DirectoryNode Node, bool ChildrenDone)>();
        stack.Push((subtreeRoot, false));
        while (stack.Count > 0)
        {
            var (node, childrenDone) = stack.Pop();
            if (childrenDone)
            {
                node.Seal();
                continue;
            }

            stack.Push((node, true));
            foreach (var child in node.Children)
            {
                if (child is DirectoryNode dir)
                {
                    stack.Push((dir, false));
                }
            }
        }
    }

    private static DriveSnapshot? TryGetDriveSnapshot(string rootPath)
    {
        try
        {
            string? pathRoot = Path.GetPathRoot(rootPath);
            if (pathRoot is null || !rootPath.TrimEnd('\\').Equals(pathRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var info = new DriveInfo(pathRoot);
            return info.IsReady ? new DriveSnapshot(info.Name, info.TotalSize, info.TotalFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow;
        }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Estado compartilhado entre as threads de um scan (fila de trabalho e contadores).</summary>
internal sealed class ScanContext
{
    private const int MaxInaccessibleSamples = 300;

    /// <summary>Pastas até esta profundidade são sempre lidas em modo raso (subdivididas).</summary>
    private const int AlwaysExpandDepth = 2;

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly HashSet<string> _excluded;
    private readonly int _starvationThreshold;
    private readonly int _maxDepthForShallow;
    private int _pendingItems;
    private int _queuedItems;
    private int _idleWorkers;
    private long _files;
    private long _bytes;
    private long _directories;
    private long _cloudOnlySkipped;
    private long _linksSkipped;
    private int _inaccessible;
    private volatile string _currentPath = string.Empty;
    private readonly ThreadLocal<ScanStatistics> _statistics = new(() => new ScanStatistics(), trackAllValues: true);

    public ScanContext(ScanOptions options, CancellationToken token)
    {
        Token = token;
        SkipCloudOnly = options.SkipCloudOnlyFiles;
        Largest = new LargestFilesTracker(options.TopFilesCount);
        _starvationThreshold = Math.Max(4, options.MaxDegreeOfParallelism * 3);
        _maxDepthForShallow = Math.Max(AlwaysExpandDepth, options.ParallelDepth + 3);
        _excluded = new HashSet<string>(
            options.ExcludedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.TrimEndingDirectorySeparator(p.Trim())),
            StringComparer.OrdinalIgnoreCase);
    }

    public CancellationToken Token { get; }
    public bool SkipCloudOnly { get; }
    public LargestFilesTracker Largest { get; }
    public ScanStatistics Statistics => _statistics.Value!;

    /// <summary>Cache de nomes por thread (nomes repetidos como "index.js" viram a mesma string).</summary>
    public NamePool Names => _names.Value!;

    private readonly ThreadLocal<NamePool> _names = new(() => new NamePool());

    /// <summary>
    /// true quando há threads ociosas esperando trabalho: a thread que está varrendo uma subárvore
    /// grande passa a doar subpastas para a fila em vez de entrar nelas sozinha.
    /// </summary>
    public bool WantsWork
    {
        get
        {
            int idle = Volatile.Read(ref _idleWorkers);
            return idle > 0 && Volatile.Read(ref _queuedItems) < Math.Max(4, idle * 2);
        }
    }
    public ConcurrentQueue<string> InaccessibleSamples { get; } = new();
    public long Directories => Interlocked.Read(ref _directories);
    public long CloudOnlySkipped => Interlocked.Read(ref _cloudOnlySkipped);
    public long LinksSkipped => Interlocked.Read(ref _linksSkipped);
    public int Inaccessible => Volatile.Read(ref _inaccessible);

    public string CurrentPath
    {
        get => _currentPath;
        set => _currentPath = value;
    }

    public void Enqueue(DirectoryNode node, string path, int depth)
    {
        Interlocked.Increment(ref _pendingItems);
        Interlocked.Increment(ref _queuedItems);
        _queue.Enqueue(new WorkItem(node, path, depth));
    }

    /// <summary>Laço de cada thread: pega pastas da fila até não haver mais trabalho pendente.</summary>
    public void WorkerLoop()
    {
        var spinner = new SpinWait();
        bool idle = false;
        try
        {
            while (!Token.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var item))
                {
                    Interlocked.Decrement(ref _queuedItems);
                    if (idle)
                    {
                        Interlocked.Decrement(ref _idleWorkers);
                        idle = false;
                    }

                    spinner.Reset();
                    try
                    {
                        bool shallow = item.Depth < AlwaysExpandDepth ||
                                       (item.Depth < _maxDepthForShallow && Volatile.Read(ref _queuedItems) < _starvationThreshold);
                        ScanEnumerator.Run(item.Node, item.Path, item.Depth, recursive: !shallow, this);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        RegisterError(item.Path, 5);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingItems);
                    }

                    continue;
                }

                if (Volatile.Read(ref _pendingItems) == 0)
                {
                    return;
                }

                if (!idle)
                {
                    Interlocked.Increment(ref _idleWorkers);
                    idle = true;
                }

                spinner.SpinOnce(sleep1Threshold: 20);
            }
        }
        finally
        {
            if (idle)
            {
                Interlocked.Decrement(ref _idleWorkers);
            }
        }
    }

    public bool IsExcluded(ref FileSystemEntry entry) =>
        _excluded.Count > 0 && _excluded.Contains(entry.ToFullPath());

    public void AddCounts(long files, long bytes, long directories)
    {
        if (files != 0)
        {
            Interlocked.Add(ref _files, files);
        }

        if (bytes != 0)
        {
            Interlocked.Add(ref _bytes, bytes);
        }

        if (directories != 0)
        {
            Interlocked.Add(ref _directories, directories);
        }
    }

    public void AddCloudSkipped() => Interlocked.Increment(ref _cloudOnlySkipped);

    public void AddLinkSkipped() => Interlocked.Increment(ref _linksSkipped);

    public void RegisterError(string path, int errorCode)
    {
        int count = Interlocked.Increment(ref _inaccessible);
        if (count <= MaxInaccessibleSamples && !string.IsNullOrEmpty(path))
        {
            InaccessibleSamples.Enqueue(path);
        }
    }

    public ScanProgress Snapshot(TimeSpan elapsed)
    {
        long files = Interlocked.Read(ref _files);
        long bytes = Interlocked.Read(ref _bytes);
        double seconds = Math.Max(0.001, elapsed.TotalSeconds);
        return new ScanProgress(files, Directories, bytes, CurrentPath, elapsed, files / seconds, bytes / seconds,
            Inaccessible);
    }

    public (Dictionary<string, ExtensionStat>, long[], long[]) MergeStatistics()
    {
        var extensions = new Dictionary<string, ExtensionStat>(StringComparer.OrdinalIgnoreCase);
        var categoryBytes = new long[ScanStatistics.CategorySlots];
        var categoryCounts = new long[ScanStatistics.CategorySlots];

        foreach (var local in _statistics.Values)
        {
            for (int i = 0; i < categoryBytes.Length; i++)
            {
                categoryBytes[i] += local.CategoryBytes[i];
                categoryCounts[i] += local.CategoryCounts[i];
            }

            foreach (var (ext, acc) in local.Extensions)
            {
                string key = ext.ToLowerInvariant();
                extensions[key] = extensions.TryGetValue(key, out var existing)
                    ? existing with
                    {
                        FileCount = existing.FileCount + acc.Count,
                        TotalBytes = existing.TotalBytes + acc.Bytes,
                    }
                    : new ExtensionStat(key, acc.Category, acc.Count, acc.Bytes);
            }
        }

        _statistics.Dispose();
        _names.Dispose();
        return (extensions, categoryBytes, categoryCounts);
    }

    private readonly record struct WorkItem(DirectoryNode Node, string Path, int Depth);
}

/// <summary>Acumuladores por thread (sem locks no caminho quente).</summary>
internal sealed class ScanStatistics
{
    public static readonly int CategorySlots = FileCategoryInfo.All.Max(c => (int)c) + 1;

    public Dictionary<string, ExtensionAccumulator> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long[] CategoryBytes { get; } = new long[CategorySlots];
    public long[] CategoryCounts { get; } = new long[CategorySlots];

    public void Add(string extension, FileCategory category, long length)
    {
        CategoryBytes[(int)category] += length;
        CategoryCounts[(int)category]++;
        if (!Extensions.TryGetValue(extension, out var acc))
        {
            acc = new ExtensionAccumulator(category);
            Extensions[extension] = acc;
        }

        acc.Count++;
        acc.Bytes += length;
    }
}

internal sealed class ExtensionAccumulator(FileCategory category)
{
    public FileCategory Category { get; } = category;
    public long Count;
    public long Bytes;
}
