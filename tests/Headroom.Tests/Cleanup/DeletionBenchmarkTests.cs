using System.Diagnostics;
using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Cleanup.Safety;
using Headroom.Tests.TestHelpers;
using Xunit.Abstractions;

namespace Headroom.Tests.Cleanup;

/// <summary>Só roda com <c>HEADROOM_BENCH=1</c> (cria centenas de milhares de pastas e hardlinks).</summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HEADROOM_BENCH") != "1")
        {
            Skip = "Medição de desempenho: defina HEADROOM_BENCH=1 (e HEADROOM_BENCH_ENVS para o tamanho).";
        }
    }
}

/// <summary>
/// Limpeza de um cache no formato do uv, grande e sintético: <c>archive-v0</c> com os pacotes e milhares de
/// ambientes em <c>builds-v0</c> ligando nos mesmos arquivos (hardlinks), cheios de sobras <c>.tmp*</c> vazias.
/// Mede o tempo da exclusão e confere o "liberado" contra o tamanho real (cada arquivo uma vez).
/// </summary>
/// <remarks>
/// <c>dotnet test tests/Headroom.Tests --filter FullyQualifiedName~DeletionBenchmark --logger "console;verbosity=detailed"</c>
/// com <c>HEADROOM_BENCH=1</c>. A Lixeira é a falsa dos testes (move a pasta no mesmo disco): mede o que o Headroom
/// faz antes de chamar o Windows, não o tempo do próprio Windows para registrar os itens na Lixeira.
/// </remarks>
public class DeletionBenchmarkTests(ITestOutputHelper output)
{
    private static int Environments =>
        int.TryParse(Environment.GetEnvironmentVariable("HEADROOM_BENCH_ENVS"), out int n) && n > 0 ? Math.Min(n, 10_000) : 1000;

    private sealed class MovingRecycler(string binPath) : IRecycler
    {
        public int FileCalls;
        public int DirectoryCalls;

        public void RecycleFile(string path)
        {
            Interlocked.Increment(ref FileCalls);
            File.Move(path, Path.Combine(binPath, Guid.NewGuid().ToString("N")));
        }

        public void RecycleDirectory(string path)
        {
            Interlocked.Increment(ref DirectoryCalls);
            Directory.Move(path, Path.Combine(binPath, Guid.NewGuid().ToString("N")));
        }
    }

    [BenchmarkFact]
    public void RecycleBin_LargeUvCache() => Run(DeletionMode.RecycleBin, lockOneFile: false);

    [BenchmarkFact]
    public void RecycleBin_LargeUvCache_OneFileInUse() => Run(DeletionMode.RecycleBin, lockOneFile: true);

    [BenchmarkFact]
    public void Permanent_LargeUvCache() => Run(DeletionMode.Permanent, lockOneFile: false);

    /// <summary>
    /// Pasta da medição. Por padrão %LOCALAPPDATA%\Temp com o nome longo: o %TEMP% pode vir com nome curto (8.3, como
    /// ADMINI~1) e aí o DeletionGuard expande o nome de cada item (GetLongPathName), o que o cache real
    /// (%LOCALAPPDATA%\uv\cache) não faz.
    /// </summary>
    private static string BenchParent =>
        Environment.GetEnvironmentVariable("HEADROOM_BENCH_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");

    private void Run(DeletionMode mode, bool lockOneFile)
    {
        using var temp = new TempDirectory(BenchParent);
        Assert.DoesNotContain('~', temp.Path);
        string cache = temp.Combine("cache");
        string bin = temp.CreateDirectory("lixeira");

        var sw = Stopwatch.StartNew();
        var stats = SyntheticUvCache.Build(cache, Environments);
        output.WriteLine($"Cache sintético: {stats.Environments:N0} ambientes, {stats.Files:N0} caminhos de arquivo, " +
                         $"{stats.Directories:N0} pastas; criado em {sw.Elapsed.TotalSeconds:N1} s");

        sw.Restart();
        var measured = ContentWalker.Measure(cache, CancellationToken.None);
        output.WriteLine($"Análise (ContentWalker.Measure, paralelo, só listagem): {sw.Elapsed.TotalSeconds:N2} s; " +
                         $"{measured.Size:N0} bytes únicos (soma por caminho: {stats.PathBytes:N0})");
        Assert.Equal(stats.UniqueBytes, measured.Size);

        var targets = new[]
        {
            new DeletionTarget(Path.Combine(cache, "archive-v0"), true),
            new DeletionTarget(Path.Combine(cache, "builds-v0"), true),
            new DeletionTarget(Path.Combine(cache, "CACHEDIR.TAG"), false),
        };
        var options = new DeletionOptions { Mode = mode, AllowedRoots = [new AllowedRoot(cache)] };
        var recycler = new MovingRecycler(bin);

        string? locked = lockOneFile ? stats.DeepOwnFile : null;
        using (locked is null ? null : new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            sw.Restart();
            var result = new FileDeleter(recycler: recycler).Delete(targets, options);
            sw.Stop();

            output.WriteLine($"{mode}{(lockOneFile ? " (1 arquivo em uso)" : "")}: {sw.Elapsed.TotalSeconds:N2} s; " +
                             $"liberado {result.BytesDeleted:N0} bytes, {result.FilesDeleted:N0} arquivos, " +
                             $"{result.DirectoriesDeleted:N0} pastas, em uso {result.SkippedInUse}, erros {result.Errors}; " +
                             $"Lixeira: {recycler.DirectoryCalls:N0} pastas, {recycler.FileCalls:N0} arquivos");

            Assert.Equal(0, result.Errors);
            Assert.Equal(lockOneFile ? 1 : 0, result.SkippedInUse);
            Assert.True(Directory.Exists(cache));
            if (!lockOneFile)
            {
                Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
            }
            else
            {
                Assert.True(File.Exists(locked));
            }
        }

        sw.Restart();
        SyntheticUvCache.DeleteTree(temp.Path);
        output.WriteLine($"(remoção da pasta de teste: {sw.Elapsed.TotalSeconds:N1} s)");
    }
}

/// <summary>Cache no formato do uv, com os números que importam para a limpeza (sem tocar no cache real).</summary>
internal static class SyntheticUvCache
{
    private const int Packages = 40;
    private const int SubDirectories = 6;
    private const int FilesPerDirectory = 50;
    private const int PackagesPerEnvironment = 3;
    private const int EmptyLeftoversPerDirectory = 8;

    public sealed record Stats(int Environments, long Files, long Directories, long UniqueBytes, long PathBytes, string DeepOwnFile);

    public static Stats Build(string cache, int environments)
    {
        var random = new Random(7);
        var content = new byte[64 * 1024];
        random.NextBytes(content);

        // archive-v0: os pacotes extraídos uma vez.
        var archiveFiles = new string[Packages][];
        long uniqueBytes = 0;
        long pathBytes = 0;
        long files = 0;
        long directories = 2;
        var sizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int p = 0; p < Packages; p++)
        {
            var list = new List<string>();
            for (int d = 0; d < SubDirectories; d++)
            {
                string dir = Path.Combine(cache, "archive-v0", $"pkg{p:D3}-hash", $"pkg{p:D3}", $"sub{d}");
                Directory.CreateDirectory(dir);
                directories++;
                for (int f = 0; f < FilesPerDirectory; f++)
                {
                    int size = 512 + random.Next(7680);
                    string file = Path.Combine(dir, $"mod{f:D2}.py");
                    File.WriteAllBytes(file, content.AsSpan(0, size).ToArray());
                    sizes[file] = size;
                    list.Add(file);
                    uniqueBytes += size;
                    pathBytes += size;
                    files++;
                }
            }

            directories += 2;
            archiveFiles[p] = list.ToArray();
        }

        File.WriteAllBytes(Path.Combine(cache, "CACHEDIR.TAG"), content.AsSpan(0, 43).ToArray());
        uniqueBytes += 43;
        pathBytes += 43;
        files++;

        // builds-v0: ambientes temporários ligando nos pacotes, com arquivos próprios e sobras .tmp* vazias.
        long envFiles = 0;
        long envDirectories = 0;
        long envOwnBytes = 0;
        long envLinkedBytes = 0;
        string deepOwnFile = string.Empty;
        Parallel.For(0, environments, new ParallelOptions { MaxDegreeOfParallelism = 8 }, e =>
        {
            string env = Path.Combine(cache, "builds-v0", $".tmp{e:D6}");
            long localFiles = 0;
            long localDirs = 1;
            long localOwn = 0;
            long localLinked = 0;
            string scripts = Path.Combine(env, "Scripts");
            Directory.CreateDirectory(scripts);
            localDirs++;
            foreach (var (name, size) in new[] { ("python.exe", 4096), ("pythonw.exe", 4096), ("activate", 1200), ("activate.bat", 900) })
            {
                File.WriteAllBytes(Path.Combine(scripts, name), content.AsSpan(0, size).ToArray());
                localOwn += size;
                localFiles++;
            }

            string sitePackages = Path.Combine(env, "Lib", "site-packages");
            Directory.CreateDirectory(sitePackages);
            localDirs += 2;
            File.WriteAllBytes(Path.Combine(env, "pyvenv.cfg"), content.AsSpan(0, 300).ToArray());
            File.WriteAllBytes(Path.Combine(sitePackages, "_virtualenv.py"), content.AsSpan(0, 4300).ToArray());
            localOwn += 4600;
            localFiles += 2;

            for (int k = 0; k < PackagesPerEnvironment; k++)
            {
                int p = (e * 7 + k * 13) % Packages;
                var source = archiveFiles[p];
                for (int d = 0; d < SubDirectories; d++)
                {
                    string dir = Path.Combine(sitePackages, $"pkg{p:D3}", $"sub{d}");
                    Directory.CreateDirectory(dir);
                    localDirs++;
                    for (int f = 0; f < FilesPerDirectory; f++)
                    {
                        string original = source[d * FilesPerDirectory + f];
                        TempDirectory.CreateHardLink(Path.Combine(dir, Path.GetFileName(original)), original);
                        localLinked += new FileInfo(original).Length;
                        localFiles++;
                    }

                    for (int t = 0; t < EmptyLeftoversPerDirectory; t++)
                    {
                        Directory.CreateDirectory(Path.Combine(dir, $".tmp{e:x4}{d}{t}"));
                        localDirs++;
                    }
                }

                localDirs++;
            }

            Interlocked.Add(ref envFiles, localFiles);
            Interlocked.Add(ref envDirectories, localDirs);
            Interlocked.Add(ref envOwnBytes, localOwn);
            Interlocked.Add(ref envLinkedBytes, localLinked);
            if (e == environments / 2)
            {
                deepOwnFile = Path.Combine(scripts, "python.exe");
            }
        });

        return new Stats(environments, files + envFiles, directories + envDirectories, uniqueBytes + envOwnBytes,
            pathBytes + envOwnBytes + envLinkedBytes, deepOwnFile);
    }

    /// <summary>Apaga a pasta de teste em paralelo (sem junctions: o cache sintético não tem nenhuma).</summary>
    public static void DeleteTree(string root)
    {
        // Netos de cache\ e lixeira\: os ambientes (cache\builds-v0\.tmp*, ou lixeira\<pasta movida>\.tmp*).
        var work = new List<string>();
        foreach (var top in new[] { Path.Combine(root, "cache"), Path.Combine(root, "lixeira") })
        {
            if (!Directory.Exists(top))
            {
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(top))
            {
                work.AddRange(Directory.EnumerateDirectories(child));
            }
        }

        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = 8 }, dir =>
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        });
    }
}
