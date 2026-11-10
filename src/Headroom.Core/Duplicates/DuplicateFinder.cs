using System.Buffers;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Headroom.Core.Duplicates;

/// <summary>Arquivo candidato à análise de duplicados.</summary>
public sealed record FileCandidate(string Path, long Size, DateTime LastWriteUtc);

/// <summary>Um arquivo dentro de um grupo de duplicados.</summary>
public sealed record DuplicateFile(string Path, long Size, DateTime LastWriteUtc, DateTime CreationUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>Grupo de arquivos com conteúdo idêntico.</summary>
public sealed class DuplicateGroup
{
    public DuplicateGroup(long size, string hash, IReadOnlyList<DuplicateFile> files)
    {
        Size = size;
        Hash = hash;
        Files = files;
    }

    public long Size { get; }
    public string Hash { get; }
    public IReadOnlyList<DuplicateFile> Files { get; }

    /// <summary>Espaço desperdiçado: tamanho × (cópias − 1).</summary>
    public long WastedBytes => Size * (Files.Count - 1);

    /// <summary>Sugestão de qual manter: o mais antigo (provável original).</summary>
    public DuplicateFile SuggestedKeep =>
        Files.OrderBy(f => f.CreationUtc).ThenBy(f => f.LastWriteUtc).ThenBy(f => f.Path.Length).First();
}

public sealed record DuplicateProgress(string Stage, int Processed, int Total, string CurrentPath);

public sealed class DuplicateOptions
{
    /// <summary>Ignora arquivos menores que isto. Padrão: 1 MB.</summary>
    public long MinimumSize { get; init; } = 1024 * 1024;

    /// <summary>Bytes lidos no hash parcial. Padrão: 64 KB.</summary>
    public int PartialHashBytes { get; init; } = 64 * 1024;

    /// <summary>Leituras simultâneas (I/O). Padrão: 4.</summary>
    public int MaxParallelReads { get; init; } = 4;
}

/// <summary>
/// Encontra arquivos duplicados em três etapas: (1) agrupa por tamanho, (2) hash parcial dos
/// primeiros 64 KB (xxHash64), (3) hash completo (xxHash128) só dos candidatos restantes.
/// Hardlinks (mesmo arquivo físico) são descartados, pois apagá-los não libera espaço.
/// </summary>
public sealed class DuplicateFinder
{
    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(
        IEnumerable<FileCandidate> files,
        DuplicateOptions? options = null,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DuplicateOptions();

        // Etapa 1: tamanho.
        var bySize = files
            .Where(f => f.Size >= options.MinimumSize)
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();

        var stage1 = bySize.SelectMany(g => g).ToList();
        progress?.Report(new DuplicateProgress("Agrupando por tamanho", stage1.Count, stage1.Count, string.Empty));

        // Etapa 2: hash parcial.
        var partial = await HashAllAsync(stage1, f => PartialHash(f.Path, options.PartialHashBytes),
            "Hash parcial (64 KB)", options, progress, cancellationToken).ConfigureAwait(false);

        var headGroups = stage1
            .Where(f => partial.ContainsKey(f.Path))
            .GroupBy(f => (f.Size, partial[f.Path]))
            .Where(g => g.Count() > 1)
            .ToList();

        // Etapa 2b: últimos 64 KB (barato e elimina arquivos que só diferem no fim, comuns em modelos/vídeos).
        var needTail = headGroups.Where(g => g.Key.Size > options.PartialHashBytes * 2L).SelectMany(g => g).ToList();
        var tail = await HashAllAsync(needTail, f => TailHash(f.Path, options.PartialHashBytes),
            "Hash do final (64 KB)", options, progress, cancellationToken).ConfigureAwait(false);

        var partialGroups = headGroups
            .SelectMany(g => g.Key.Size > options.PartialHashBytes * 2L
                ? g.Where(f => tail.ContainsKey(f.Path)).GroupBy(f => (g.Key.Size, g.Key.Item2 + ":" + tail[f.Path]))
                : new[] { g }.AsEnumerable())
            .Where(g => g.Count() > 1)
            .ToList();

        // Etapa 3: hash completo (arquivos menores que o bloco parcial já foram lidos inteiros).
        var needFull = partialGroups
            .Where(g => g.Key.Size > options.PartialHashBytes)
            .SelectMany(g => g)
            .ToList();

        var full = await HashAllAsync(needFull, f => FullHash(f.Path, cancellationToken),
            "Hash completo", options, progress, cancellationToken).ConfigureAwait(false);

        var groups = new List<DuplicateGroup>();
        foreach (var pg in partialGroups)
        {
            IEnumerable<IGrouping<string, FileCandidate>> finalGroups = pg.Key.Size <= options.PartialHashBytes
                ? pg.GroupBy(_ => pg.Key.Item2)
                : pg.Where(f => full.ContainsKey(f.Path)).GroupBy(f => full[f.Path]);

            foreach (var fg in finalGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unique = RemoveHardLinks(fg.ToList());
                if (unique.Count < 2)
                {
                    continue;
                }

                var dupFiles = unique
                    .Select(f => new DuplicateFile(f.Path, f.Size, f.LastWriteUtc, SafeCreationUtc(f.Path)))
                    .ToList();
                groups.Add(new DuplicateGroup(pg.Key.Size, fg.Key, dupFiles));
            }
        }

        return groups.OrderByDescending(g => g.WastedBytes).ToList();
    }

    private static async Task<Dictionary<string, string>> HashAllAsync(
        List<FileCandidate> files,
        Func<FileCandidate, string?> hasher,
        string stage,
        DuplicateOptions options,
        IProgress<DuplicateProgress>? progress,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        int processed = 0;

        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelReads, CancellationToken = ct },
            (file, token) =>
            {
                string? hash = hasher(file);
                int done = Interlocked.Increment(ref processed);
                if (hash is not null)
                {
                    lock (gate)
                    {
                        result[file.Path] = hash;
                    }
                }

                if (done % 25 == 0 || done == files.Count)
                {
                    progress?.Report(new DuplicateProgress(stage, done, files.Count, file.Path));
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        return result;
    }

    internal static string? PartialHash(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.SequentialScan);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bytes);
            try
            {
                int total = 0;
                int read;
                while (total < bytes && (read = stream.Read(buffer, total, bytes - total)) > 0)
                {
                    total += read;
                }

                return Convert.ToHexString(XxHash64.Hash(buffer.AsSpan(0, total)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? TailHash(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.RandomAccess);
            long start = Math.Max(0, stream.Length - bytes);
            stream.Seek(start, SeekOrigin.Begin);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bytes);
            try
            {
                int total = 0;
                int read;
                while (total < bytes && (read = stream.Read(buffer, total, bytes - total)) > 0)
                {
                    total += read;
                }

                return Convert.ToHexString(XxHash64.Hash(buffer.AsSpan(0, total)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? FullHash(string path, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.SequentialScan);
            var hasher = new XxHash128();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    hasher.Append(buffer.AsSpan(0, read));
                }

                return Convert.ToHexString(hasher.GetCurrentHash());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Mantém só um caminho por arquivo físico (volume + índice do arquivo).</summary>
    private static List<FileCandidate> RemoveHardLinks(List<FileCandidate> files)
    {
        var seen = new HashSet<(uint, ulong)>();
        var result = new List<FileCandidate>(files.Count);
        foreach (var file in files)
        {
            var id = TryGetFileId(file.Path);
            if (id is null || seen.Add(id.Value))
            {
                result.Add(file);
            }
        }

        return result;
    }

    internal static (uint Volume, ulong Index)? TryGetFileId(string path)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (GetFileInformationByHandle(handle, out var info))
            {
                return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static DateTime SafeCreationUtc(string path)
    {
        try
        {
            return File.GetCreationTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MaxValue;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
}
