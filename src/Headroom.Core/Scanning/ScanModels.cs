namespace Headroom.Core.Scanning;

/// <summary>Opções do scanner.</summary>
public sealed class ScanOptions
{
    /// <summary>Quantos maiores arquivos manter (min-heap). Padrão: 500.</summary>
    public int TopFilesCount { get; init; } = 500;

    /// <summary>Grau máximo de paralelismo. Padrão: nº de núcleos lógicos (mín. 2).</summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Max(2, Environment.ProcessorCount);

    /// <summary>Usa no máximo 2 threads em HD mecânico (detectado pelo "seek penalty"). Padrão: sim.</summary>
    public bool LimitParallelismOnHdd { get; init; } = true;

    /// <summary>
    /// Profundidade extra (além dos dois primeiros níveis, sempre subdivididos) até a qual uma pasta
    /// ainda pode ser repartida entre threads quando a fila de trabalho fica curta. Melhora o
    /// balanceamento em discos onde uma única pasta (ex.: C:\Users\Fulano\AppData) concentra quase tudo.
    /// </summary>
    public int ParallelDepth { get; init; } = 3;

    /// <summary>Caminhos completos de pastas que não devem ser escaneadas.</summary>
    public IReadOnlyCollection<string> ExcludedPaths { get; init; } = Array.Empty<string>();

    /// <summary>Ignorar arquivos que existem só na nuvem (OneDrive etc.). Padrão: sim.</summary>
    public bool SkipCloudOnlyFiles { get; init; } = true;

    /// <summary>Intervalo entre relatórios de progresso.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>Instantâneo do progresso de um scan.</summary>
public sealed record ScanProgress(
    long FilesScanned,
    long DirectoriesScanned,
    long BytesCounted,
    string CurrentPath,
    TimeSpan Elapsed,
    double FilesPerSecond,
    double BytesPerSecond,
    int InaccessibleCount);

/// <summary>Estatística agregada por extensão.</summary>
public sealed record ExtensionStat(string Extension, FileCategory Category, long FileCount, long TotalBytes);

/// <summary>Estatística agregada por categoria.</summary>
public sealed record CategoryStat(FileCategory Category, long FileCount, long TotalBytes)
{
    public string DisplayName => FileCategoryInfo.DisplayName(Category);
}

/// <summary>Tipos de pasta de artefatos de desenvolvimento.</summary>
public enum DevArtifactKind
{
    NodeModules,
    DotnetBin,
    DotnetObj,
    PythonVenv,
    PyCache,
    Gradle,
    Pods,
}

public static class DevArtifactKindInfo
{
    public static string DisplayName(DevArtifactKind kind) => kind switch
    {
        DevArtifactKind.NodeModules => "node_modules",
        DevArtifactKind.DotnetBin => "bin (.NET)",
        DevArtifactKind.DotnetObj => "obj (.NET)",
        DevArtifactKind.PythonVenv => "Ambiente virtual Python",
        DevArtifactKind.PyCache => "__pycache__",
        DevArtifactKind.Gradle => ".gradle",
        DevArtifactKind.Pods => "Pods (CocoaPods)",
        _ => kind.ToString(),
    };
}

/// <summary>Pasta de artefato de desenvolvimento encontrada no scan.</summary>
public sealed record DevArtifact(ScanNode Node, DevArtifactKind Kind)
{
    public string Path => Node.FullPath;
    public long Size => Node.Size;
    public string KindName => DevArtifactKindInfo.DisplayName(Kind);
}

/// <summary>Informações do disco no momento do scan (quando a raiz é um disco).</summary>
public sealed record DriveSnapshot(string Name, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
}
