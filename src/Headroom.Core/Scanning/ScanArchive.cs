using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Headroom.Core.Settings;

namespace Headroom.Core.Scanning;

/// <summary>Resumo de uma análise salva, lido só do cabeçalho (sem carregar a árvore).</summary>
public sealed record SavedScanInfo(
    string RootPath,
    DateTime ScannedAt,
    long TotalBytes,
    long FileCount,
    string FilePath,
    long FileSizeBytes);

/// <summary>
/// Guarda a análise de um disco/pasta em %LOCALAPPDATA%\Headroom\analises para reabrir depois sem escanear.
/// Formato próprio, compactado (GZip): cabeçalho com os totais + a árvore em pré-ordem (nome, tamanho, data,
/// categoria). Ao abrir, estatísticas, maiores arquivos e artefatos de desenvolvimento são recalculados.
/// </summary>
public static class ScanArchive
{
    private const string Magic = "HRSCAN";
    private const int FormatVersion = 1;
    private const string Extension = ".hrscan";
    private static readonly long EpochTicks = DateTime.UnixEpoch.Ticks;

    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "analises");

    /// <summary>Arquivo usado para um disco/pasta ("C:\" → "C.hrscan"; pastas ganham um código para não colidir).</summary>
    public static string PathFor(string rootPath, string? directory = null)
    {
        string normalized = rootPath.TrimEnd('\\').ToUpperInvariant();
        string readable = new(normalized.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        readable = readable.Trim('_');
        if (readable.Length > 40)
        {
            readable = readable[..40];
        }

        string name = normalized.Length <= 2
            ? readable
            : $"{readable}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8]}";
        return Path.Combine(directory ?? DefaultDirectory, name + Extension);
    }

    public static Task SaveAsync(ScanResult result, string? directory = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Save(result, PathFor(result.RootPath, directory), cancellationToken), cancellationToken);

    /// <summary>Grava num arquivo temporário e troca no fim (uma falha nunca deixa um arquivo pela metade).</summary>
    internal static void Save(ScanResult result, string file, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var writer = new BinaryWriter(new BufferedStream(gzip, 1 << 16), Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(result.RootPath);
            writer.Write(result.StartedAt.ToBinary());
            writer.Write(result.TotalBytes);
            writer.Write(result.FileCount);
            writer.Write(result.Elapsed.Ticks);
            writer.Write(result.DirectoryCount);
            writer.Write(result.InaccessibleCount);
            writer.Write(result.InaccessibleSamples.Count);
            foreach (var sample in result.InaccessibleSamples)
            {
                writer.Write(sample);
            }

            writer.Write(result.CloudOnlySkipped);
            writer.Write(result.LinksSkipped);
            writer.Write(result.TopFilesCapacity);
            writer.Write(result.WasElevated);
            writer.Write(result.Drive is not null);
            if (result.Drive is { } drive)
            {
                writer.Write(drive.Name);
                writer.Write(drive.TotalBytes);
                writer.Write(drive.FreeBytes);
            }

            WriteTree(writer, result.Root, ct);
        }

        File.Move(temp, file, overwrite: true);
    }

    private static void WriteTree(BinaryWriter writer, ScanNode root, CancellationToken ct)
    {
        writer.Write7BitEncodedInt64(ToSeconds(root.LastWriteUtcTicks));
        writer.Write7BitEncodedInt(root.Children.Count);

        // Pré-ordem iterativa (pastas muito profundas não estouram a pilha).
        var stack = new Stack<IEnumerator<ScanNode>>();
        stack.Push(root.Children.GetEnumerator());
        long written = 0;
        while (stack.Count > 0)
        {
            var enumerator = stack.Peek();
            if (!enumerator.MoveNext())
            {
                stack.Pop();
                continue;
            }

            if ((++written & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var node = enumerator.Current;
            if (node.IsDirectory)
            {
                writer.Write((byte)255);
                writer.Write(node.Name);
                writer.Write7BitEncodedInt64(ToSeconds(node.LastWriteUtcTicks));
                writer.Write7BitEncodedInt(node.Children.Count);
                stack.Push(node.Children.GetEnumerator());
            }
            else
            {
                writer.Write((byte)node.Category);
                writer.Write(node.Name);
                writer.Write7BitEncodedInt64(node.Size);
                writer.Write7BitEncodedInt64(ToSeconds(node.LastWriteUtcTicks));
            }
        }
    }

    /// <summary>Lê só o cabeçalho (rápido): para mostrar "Última análise: …" sem abrir a árvore.</summary>
    public static SavedScanInfo? ReadInfo(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8);
            if (reader.ReadString() != Magic || reader.ReadInt32() != FormatVersion)
            {
                return null;
            }

            string root = reader.ReadString();
            var startedAt = DateTime.FromBinary(reader.ReadInt64());
            long total = reader.ReadInt64();
            long files = reader.ReadInt64();
            return new SavedScanInfo(root, startedAt, total, files, file, new FileInfo(file).Length);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>Análises salvas, da mais recente para a mais antiga.</summary>
    public static IReadOnlyList<SavedScanInfo> List(string? directory = null)
    {
        string dir = directory ?? DefaultDirectory;
        if (!Directory.Exists(dir))
        {
            return Array.Empty<SavedScanInfo>();
        }

        return Directory.EnumerateFiles(dir, "*" + Extension)
            .Select(ReadInfo)
            .OfType<SavedScanInfo>()
            .OrderByDescending(i => i.ScannedAt)
            .ToList();
    }

    /// <summary>Análise salva de um disco/pasta, se existir.</summary>
    public static SavedScanInfo? Find(string rootPath, string? directory = null)
    {
        string file = PathFor(rootPath, directory);
        return File.Exists(file) ? ReadInfo(file) : null;
    }

    public static void Delete(string rootPath, string? directory = null)
    {
        string file = PathFor(rootPath, directory);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    public static Task<ScanResult> LoadAsync(string file, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(file, progress, cancellationToken), cancellationToken);

    internal static ScanResult Load(string file, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new BinaryReader(new BufferedStream(gzip, 1 << 16), Encoding.UTF8);
        if (reader.ReadString() != Magic || reader.ReadInt32() != FormatVersion)
        {
            throw new InvalidDataException("O arquivo não é uma análise do Headroom (ou é de uma versão diferente).");
        }

        string rootPath = reader.ReadString();
        var startedAt = DateTime.FromBinary(reader.ReadInt64());
        long totalBytes = reader.ReadInt64();
        long totalFiles = reader.ReadInt64();
        var elapsed = TimeSpan.FromTicks(reader.ReadInt64());
        long directoryCount = reader.ReadInt64();
        int inaccessible = reader.ReadInt32();
        var samples = new string[reader.ReadInt32()];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = reader.ReadString();
        }

        long cloudOnly = reader.ReadInt64();
        long links = reader.ReadInt64();
        int topCapacity = reader.ReadInt32();
        bool elevated = reader.ReadBoolean();
        DriveSnapshot? drive = reader.ReadBoolean() ? new DriveSnapshot(reader.ReadString(), reader.ReadInt64(), reader.ReadInt64()) : null;

        var root = (DirectoryNode)ScanNode.CreateRoot(rootPath, DateTime.UnixEpoch);
        root.LastWriteUtcTicks = FromSeconds(reader.Read7BitEncodedInt64());
        int rootChildren = reader.Read7BitEncodedInt();

        var largest = new LargestFilesTracker(topCapacity);
        var categoryBytes = new long[ScanStatistics.CategorySlots];
        var categoryCounts = new long[ScanStatistics.CategorySlots];
        var extensions = new Dictionary<string, (FileCategory Category, long Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        var stack = new Stack<(DirectoryNode Dir, int Remaining)>();
        stack.Push((root, rootChildren));
        long files = 0, bytes = 0;
        while (stack.Count > 0)
        {
            var (dir, remaining) = stack.Pop();
            if (remaining == 0)
            {
                continue;
            }

            stack.Push((dir, remaining - 1));
            byte kind = reader.ReadByte();
            string name = Intern(names, reader.ReadString());
            if (kind == 255)
            {
                var child = ScanNode.CreateDirectory(name, dir, FromSeconds(reader.Read7BitEncodedInt64()));
                dir.AddChild(child);
                stack.Push((child, reader.Read7BitEncodedInt()));
                continue;
            }

            long size = reader.Read7BitEncodedInt64();
            var category = (FileCategory)kind;
            var fileNode = ScanNode.CreateFile(name, dir, size, FromSeconds(reader.Read7BitEncodedInt64()), category);
            dir.AddChild(fileNode);
            largest.Offer(fileNode);
            categoryBytes[kind] += size;
            categoryCounts[kind]++;
            string ext = fileNode.Extension.ToLowerInvariant();
            extensions[ext] = extensions.TryGetValue(ext, out var acc) ? (acc.Category, acc.Count + 1, acc.Bytes + size) : (category, 1, size);

            files++;
            bytes += size;
            if ((files & 0x3FFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                progress?.Report(new ScanProgress(files, 0, bytes, rootPath, stopwatch.Elapsed, files / seconds, bytes / seconds, 0));
            }
        }

        DiskScanner.SealTree(root, Math.Max(2, Environment.ProcessorCount), ct);
        var artifacts = DevArtifactDetector.FindAll(root).OrderByDescending(a => a.Size).ToList();
        progress?.Report(new ScanProgress(files, directoryCount, bytes, rootPath, stopwatch.Elapsed, 0, 0, 0));

        var result = new ScanResult(rootPath, root, startedAt, elapsed, directoryCount, inaccessible, samples, cloudOnly, links,
            largest.ToSortedList(), topCapacity,
            extensions.ToDictionary(e => e.Key, e => new ExtensionStat(e.Key, e.Value.Category, e.Value.Count, e.Value.Bytes),
                StringComparer.OrdinalIgnoreCase),
            categoryBytes, categoryCounts, artifacts, drive, elevated)
        {
            OpenedFromSavedAt = DateTime.Now,
        };

        Debug.Assert(result.FileCount == totalFiles && result.TotalBytes == totalBytes, "Análise salva inconsistente.");
        return result;
    }

    private static string Intern(Dictionary<string, string> pool, string name)
    {
        // Nomes curtos se repetem muito (index.js, __init__.py…): guardar uma cópia só economiza memória.
        if (name.Length > 32)
        {
            return name;
        }

        if (pool.TryGetValue(name, out var existing))
        {
            return existing;
        }

        if (pool.Count < 2_000_000)
        {
            pool[name] = name;
        }

        return name;
    }

    private static long ToSeconds(long ticks) => Math.Max(0, (ticks - EpochTicks) / TimeSpan.TicksPerSecond);

    private static long FromSeconds(long seconds) => EpochTicks + seconds * TimeSpan.TicksPerSecond;
}
