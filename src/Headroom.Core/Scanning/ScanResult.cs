namespace Headroom.Core.Scanning;

/// <summary>Resultado completo de um scan.</summary>
public sealed class ScanResult
{
    private readonly List<ScanNode> _largestFiles;
    private readonly List<DevArtifact> _devArtifacts;
    private readonly Dictionary<string, ExtensionStat> _extensions;
    private readonly long[] _categoryBytes;
    private readonly long[] _categoryCounts;

    internal ScanResult(
        string rootPath,
        ScanNode root,
        DateTime startedAt,
        TimeSpan elapsed,
        long directoryCount,
        int inaccessibleCount,
        IReadOnlyList<string> inaccessibleSamples,
        long cloudOnlySkipped,
        long linksSkipped,
        List<ScanNode> largestFiles,
        int topFilesCapacity,
        Dictionary<string, ExtensionStat> extensions,
        long[] categoryBytes,
        long[] categoryCounts,
        List<DevArtifact> devArtifacts,
        DriveSnapshot? drive,
        bool wasElevated)
    {
        RootPath = rootPath;
        Root = root;
        StartedAt = startedAt;
        Elapsed = elapsed;
        DirectoryCount = directoryCount;
        InaccessibleCount = inaccessibleCount;
        InaccessibleSamples = inaccessibleSamples;
        CloudOnlySkipped = cloudOnlySkipped;
        LinksSkipped = linksSkipped;
        _largestFiles = largestFiles;
        TopFilesCapacity = topFilesCapacity;
        _extensions = extensions;
        _categoryBytes = categoryBytes;
        _categoryCounts = categoryCounts;
        _devArtifacts = devArtifacts;
        Drive = drive;
        WasElevated = wasElevated;
    }

    /// <summary>Disparado quando itens são removidos (após exclusão pela interface).</summary>
    public event EventHandler? Changed;

    public string RootPath { get; }
    public ScanNode Root { get; }
    public DateTime StartedAt { get; }
    public TimeSpan Elapsed { get; }
    public long TotalBytes => Root.Size;
    public long FileCount => Root.FileCount;
    public long DirectoryCount { get; }
    public int InaccessibleCount { get; }
    public IReadOnlyList<string> InaccessibleSamples { get; }
    public long CloudOnlySkipped { get; }
    public long LinksSkipped { get; }
    public int TopFilesCapacity { get; }
    public DriveSnapshot? Drive { get; }
    public bool WasElevated { get; }
    public bool IsDriveRoot => Drive is not null;

    /// <summary>Quando esta análise foi aberta de um arquivo salvo (null = scan feito agora).</summary>
    public DateTime? OpenedFromSavedAt { get; internal set; }

    public bool IsFromSavedAnalysis => OpenedFromSavedAt is not null;

    public IReadOnlyList<ScanNode> LargestFiles => _largestFiles;
    public IReadOnlyList<DevArtifact> DevArtifacts => _devArtifacts;
    public long DevArtifactsBytes => _devArtifacts.Sum(a => a.Size);

    public IReadOnlyList<ExtensionStat> Extensions =>
        _extensions.Values.OrderByDescending(e => e.TotalBytes).ToList();

    public IReadOnlyList<CategoryStat> Categories =>
        FileCategoryInfo.All
            .Select(c => new CategoryStat(c, _categoryCounts[(int)c], _categoryBytes[(int)c]))
            .OrderByDescending(c => c.TotalBytes)
            .ToList();

    /// <summary>
    /// Diferença entre o "usado" do disco e o total encontrado no scan. Positivo quando o scan
    /// achou menos que o Windows reporta (Shadow Copies, System Volume Information, pastas sem
    /// permissão, metadados do NTFS); negativo quando achou mais (hardlinks contados duas vezes).
    /// </summary>
    public long? DifferenceFromUsed => Drive is null ? null : Drive.UsedBytes - TotalBytes;

    /// <summary>Localiza um nó pelo caminho completo (null se não estiver na árvore).</summary>
    public ScanNode? FindNode(string fullPath)
    {
        string root = Root.Name.TrimEnd('\\', '/');
        string target = fullPath.TrimEnd('\\', '/');
        if (target.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return Root;
        }

        if (!target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var node = Root;
        foreach (var part in target[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.FindChild(part);
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>Remove um nó (arquivo ou pasta) apagado e atualiza todas as estatísticas.</summary>
    public void RemoveNode(ScanNode node)
    {
        if (node.IsRoot)
        {
            return;
        }

        foreach (var file in node.EnumerateFiles())
        {
            int c = (int)file.Category;
            _categoryBytes[c] -= file.Size;
            _categoryCounts[c] -= 1;
            string ext = file.Extension.ToLowerInvariant();
            if (_extensions.TryGetValue(ext, out var stat))
            {
                _extensions[ext] = stat with
                {
                    FileCount = Math.Max(0, stat.FileCount - 1),
                    TotalBytes = Math.Max(0, stat.TotalBytes - file.Size),
                };
            }
        }

        _largestFiles.RemoveAll(f => ReferenceEquals(f, node) || node.IsAncestorOf(f));
        _devArtifacts.RemoveAll(a => ReferenceEquals(a.Node, node) || node.IsAncestorOf(a.Node));
        node.Detach();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
