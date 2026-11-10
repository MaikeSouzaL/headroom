using Headroom.Core.Scanning;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Scanning;

public class DiskScannerTests
{
    private static Task<ScanResult> Scan(string path, ScanOptions? options = null, IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default) => new DiskScanner().ScanAsync(path, options, progress, ct);

    [Fact]
    public async Task Scan_ComputesSizesCountsAndSortsChildren()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.txt", 100);
        temp.CreateFile(@"sub1\b.mp4", 1000);
        temp.CreateFile(@"sub1\deep\c.jpg", 500);
        temp.CreateFile(@"sub2\d.zip", 2000);
        temp.CreateDirectory("empty");

        var result = await Scan(temp.Path);

        Assert.Equal(3600, result.TotalBytes);
        Assert.Equal(4, result.FileCount);
        Assert.Equal(new[] { "sub2", "sub1", "a.txt", "empty" }, result.Root.Children.Select(c => c.Name));

        var sub1 = result.Root.FindChild("sub1")!;
        Assert.Equal(1500, sub1.Size);
        Assert.Equal(2, sub1.FileCount);
        Assert.Equal(System.IO.Path.Combine(temp.Path, "sub1"), sub1.FullPath);
        Assert.Equal(1500 / 3600.0, sub1.FractionOfParent, 6);
    }

    [Fact]
    public async Task Scan_AggregatesByCategoryAndExtension()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("filme.MP4", 1000);
        temp.CreateFile("foto.jpg", 300);
        temp.CreateFile("foto2.JPG", 200);
        temp.CreateFile("pacote.zip", 50);
        temp.CreateFile("setup.exe", 70);
        temp.CreateFile("semextensao", 10);

        var result = await Scan(temp.Path);

        var categories = result.Categories.ToDictionary(c => c.Category);
        Assert.Equal(1000, categories[FileCategory.Video].TotalBytes);
        Assert.Equal(500, categories[FileCategory.Image].TotalBytes);
        Assert.Equal(2, categories[FileCategory.Image].FileCount);
        Assert.Equal(50, categories[FileCategory.Archive].TotalBytes);
        Assert.Equal(70, categories[FileCategory.Installer].TotalBytes);
        Assert.Equal(10, categories[FileCategory.Other].TotalBytes);

        var jpg = result.Extensions.Single(e => e.Extension == ".jpg");
        Assert.Equal(2, jpg.FileCount);
        Assert.Equal(500, jpg.TotalBytes);
    }

    [Fact]
    public async Task Scan_KeepsOnlyTopNLargestFilesInDescendingOrder()
    {
        using var temp = new TempDirectory();
        for (int i = 1; i <= 30; i++)
        {
            temp.CreateFile($@"f{i % 3}\arquivo{i}.bin", i * 100);
        }

        var result = await Scan(temp.Path, new ScanOptions { TopFilesCount = 5 });

        Assert.Equal(new long[] { 3000, 2900, 2800, 2700, 2600 }, result.LargestFiles.Select(f => f.Size));
    }

    [Fact]
    public async Task Scan_DetectsDevArtifactsOnlyOnce()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"proj\app.csproj", 10);
        temp.CreateFile(@"proj\bin\Debug\app.dll", 300);
        temp.CreateFile(@"proj\obj\project.assets.json", 200);
        temp.CreateFile(@"web\package.json", 10);
        temp.CreateFile(@"web\node_modules\pkg\index.js", 400);
        temp.CreateFile(@"web\node_modules\pkg\node_modules\inner\z.js", 100);
        temp.CreateFile(@"py\venv\pyvenv.cfg", 5);
        temp.CreateFile(@"py\venv\Lib\site-packages\mod\__pycache__\m.pyc", 50);
        temp.CreateFile(@"py\src\__pycache__\a.pyc", 20);
        temp.CreateFile(@"naoprojeto\bin\ferramenta.exe", 999);

        var result = await Scan(temp.Path);
        var byPath = result.DevArtifacts.ToDictionary(a => a.Path, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(5, result.DevArtifacts.Count);
        Assert.Equal(DevArtifactKind.NodeModules, byPath[temp.Combine("web", "node_modules")].Kind);
        Assert.Equal(500, byPath[temp.Combine("web", "node_modules")].Size);
        Assert.Equal(DevArtifactKind.DotnetBin, byPath[temp.Combine("proj", "bin")].Kind);
        Assert.Equal(DevArtifactKind.DotnetObj, byPath[temp.Combine("proj", "obj")].Kind);
        Assert.Equal(DevArtifactKind.PythonVenv, byPath[temp.Combine("py", "venv")].Kind);
        Assert.Equal(55, byPath[temp.Combine("py", "venv")].Size);
        Assert.Equal(DevArtifactKind.PyCache, byPath[temp.Combine("py", "src", "__pycache__")].Kind);
        Assert.False(byPath.ContainsKey(temp.Combine("naoprojeto", "bin")));
        Assert.Equal(300 + 200 + 500 + 55 + 20, result.DevArtifactsBytes);
    }

    [Fact]
    public async Task Scan_DoesNotFollowJunctions()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        root.CreateFile("dentro.bin", 100);
        outside.CreateFile("grande.bin", 1_000_000);
        TempDirectory.CreateJunction(root.Combine("atalho"), outside.Path);

        var result = await Scan(root.Path);

        Assert.Equal(100, result.TotalBytes);
        Assert.Equal(1, result.LinksSkipped);
        Assert.Null(result.Root.FindChild("atalho"));
        Assert.True(File.Exists(outside.Combine("grande.bin")));
    }

    [Fact]
    public async Task Scan_SkipsCloudOnlyFiles()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("local.bin", 100);
        string cloud = temp.CreateFile("nuvem.bin", 5000);
        File.SetAttributes(cloud, FileAttributes.Offline);

        var result = await Scan(temp.Path);

        Assert.Equal(100, result.TotalBytes);
        Assert.Equal(1, result.CloudOnlySkipped);
    }

    [Fact]
    public async Task Scan_RespectsExcludedPaths()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"manter\a.bin", 100);
        temp.CreateFile(@"ignorar\b.bin", 900);

        var result = await Scan(temp.Path, new ScanOptions { ExcludedPaths = [temp.Combine("ignorar") + "\\"] });

        Assert.Equal(100, result.TotalBytes);
        Assert.Null(result.Root.FindChild("ignorar"));
    }

    [Fact]
    public async Task Scan_IsCancellable()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"x\a.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(temp.Path, ct: cts.Token));
    }

    [Fact]
    public async Task Scan_ReportsProgress()
    {
        using var temp = new TempDirectory();
        for (int i = 0; i < 50; i++)
        {
            temp.CreateFile($@"d{i % 5}\f{i}.txt", 10);
        }

        var reports = new List<ScanProgress>();
        var progress = new SynchronousProgress<ScanProgress>(reports.Add);
        var result = await Scan(temp.Path, progress: progress);

        Assert.NotEmpty(reports);
        Assert.Equal(50, reports[^1].FilesScanned);
        Assert.Equal(500, reports[^1].BytesCounted);
        Assert.Equal(50, result.FileCount);
    }

    [Fact]
    public async Task RemoveNode_UpdatesTreeAndStatistics()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"a\video.mp4", 1000);
        temp.CreateFile(@"a\b\foto.jpg", 300);
        temp.CreateFile(@"c\doc.pdf", 50);

        var result = await Scan(temp.Path);
        var folderB = result.FindNode(temp.Combine("a", "b"))!;
        result.RemoveNode(folderB);

        Assert.Equal(1050, result.TotalBytes);
        Assert.Equal(2, result.FileCount);
        Assert.Equal(1000, result.Root.FindChild("a")!.Size);
        Assert.Equal(0, result.Categories.Single(c => c.Category == FileCategory.Image).TotalBytes);
        Assert.DoesNotContain(result.LargestFiles, f => f.Name == "foto.jpg");
    }

    [Fact]
    public async Task FindNode_LocatesNestedNodesCaseInsensitively()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"Pasta\Sub\arquivo.txt", 10);

        var result = await Scan(temp.Path);

        Assert.NotNull(result.FindNode(temp.Combine("pasta", "SUB", "ARQUIVO.TXT")));
        Assert.Same(result.Root, result.FindNode(temp.Path + "\\"));
        Assert.Null(result.FindNode(@"Z:\nao\existe"));
    }

    [Theory]
    [InlineData("c:", "C:\\")]
    [InlineData("c:\\", "C:\\")]
    [InlineData("C:\\Windows\\", "C:\\Windows")]
    public void NormalizeRoot_HandlesDrivesAndFolders(string input, string expected)
    {
        Assert.Equal(expected, DiskScanner.NormalizeRoot(input));
    }

    [Fact]
    public async Task Scan_ThrowsFriendlyErrorForMissingFolder()
    {
        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Scan(@"C:\pasta-que-nao-existe-123456"));
        Assert.Contains("não existe", ex.Message);
    }

    /// <summary>IProgress que executa na mesma thread (Progress&lt;T&gt; usaria o pool).</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly object _gate = new();

        public void Report(T value)
        {
            lock (_gate)
            {
                handler(value);
            }
        }
    }
}
