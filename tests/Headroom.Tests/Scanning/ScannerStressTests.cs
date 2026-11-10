using Headroom.Core.Scanning;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Scanning;

/// <summary>
/// Árvore grande e irregular, varrida com muitas threads (o que força a doação dinâmica de trabalho):
/// os totais precisam bater exatamente com a soma real, sem contar nada duas vezes.
/// </summary>
public class ScannerStressTests
{
    [Fact]
    public async Task ParallelScan_CountsEveryFileExactlyOnce()
    {
        using var temp = new TempDirectory();
        var random = new Random(1234);
        long expectedBytes = 0;
        int expectedFiles = 0;
        int expectedDirs = 1;

        void Build(string dir, int depth)
        {
            int files = random.Next(0, 6);
            for (int i = 0; i < files; i++)
            {
                int size = random.Next(0, 300);
                File.WriteAllBytes(Path.Combine(dir, $"f{i}.bin"), new byte[size]);
                expectedBytes += size;
                expectedFiles++;
            }

            if (depth >= 6)
            {
                return;
            }

            int subdirs = depth < 2 ? random.Next(3, 7) : random.Next(0, 4);
            for (int i = 0; i < subdirs; i++)
            {
                string sub = Path.Combine(dir, $"d{depth}_{i}");
                Directory.CreateDirectory(sub);
                expectedDirs++;
                Build(sub, depth + 1);
            }
        }

        Build(temp.Path, 0);

        for (int run = 0; run < 3; run++)
        {
            var result = await new DiskScanner().ScanAsync(temp.Path, new ScanOptions { MaxDegreeOfParallelism = 16 });

            Assert.Equal(expectedBytes, result.TotalBytes);
            Assert.Equal(expectedFiles, result.FileCount);
            Assert.Equal(expectedDirs, result.DirectoryCount);
            Assert.Equal(expectedFiles, result.Root.EnumerateFiles().Count());

            // Nenhum nó pode aparecer duas vezes na árvore.
            var paths = result.Root.EnumerateFiles().Select(f => f.FullPath).ToList();
            Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }
}
