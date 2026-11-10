using Headroom.Core.Scanning;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Scanning;

public class ScanArchiveTests
{
    private static async Task<ScanResult> ScanSampleAsync(TempDirectory temp)
    {
        var old = DateTime.UtcNow.AddYears(-2);
        temp.CreateFile(@"fotos\praia.jpg", 3_000_000, seed: 1, lastWriteUtc: old);
        temp.CreateFile(@"fotos\festa.png", 1_500_000, seed: 2);
        temp.CreateFile(@"videos\filme.mp4", 9_000_000, seed: 3);
        temp.CreateFile(@"docs\contrato.pdf", 200_000, seed: 4);
        temp.CreateFile(@"projeto\node_modules\lib\index.js", 50_000, seed: 5);
        temp.CreateFile(@"projeto\package.json", 300, seed: 6);
        temp.CreateFile(@"projeto\src\app.ts", 4_000, seed: 7);
        temp.CreateFile(@"a\b\c\d\e\f\profundo.txt", 10, seed: 8);
        Directory.CreateDirectory(temp.Combine("vazia"));
        return await new DiskScanner().ScanAsync(temp.Path, new ScanOptions { TopFilesCount = 5 });
    }

    [Fact]
    public async Task SaveAndLoad_RebuildsTheSameAnalysis()
    {
        using var temp = new TempDirectory();
        using var store = new TempDirectory();
        var original = await ScanSampleAsync(temp);

        await ScanArchive.SaveAsync(original, store.Path);
        string file = ScanArchive.PathFor(original.RootPath, store.Path);
        Assert.True(File.Exists(file));

        var loaded = await ScanArchive.LoadAsync(file);

        Assert.True(loaded.IsFromSavedAnalysis);
        Assert.False(original.IsFromSavedAnalysis);
        Assert.Equal(original.RootPath, loaded.RootPath);
        Assert.Equal(original.TotalBytes, loaded.TotalBytes);
        Assert.Equal(original.FileCount, loaded.FileCount);
        Assert.Equal(original.DirectoryCount, loaded.DirectoryCount);
        Assert.Equal(original.StartedAt, loaded.StartedAt);
        Assert.Equal(original.LargestFiles.Select(f => f.FullPath), loaded.LargestFiles.Select(f => f.FullPath));
        Assert.Equal(original.Categories.Select(c => (c.Category, c.FileCount, c.TotalBytes)),
            loaded.Categories.Select(c => (c.Category, c.FileCount, c.TotalBytes)));
        Assert.Equal(original.Extensions.Select(e => (e.Extension, e.FileCount, e.TotalBytes)),
            loaded.Extensions.Select(e => (e.Extension, e.FileCount, e.TotalBytes)));
        Assert.Equal(original.DevArtifacts.Select(a => a.Path), loaded.DevArtifacts.Select(a => a.Path));

        // Árvore igual: mesmos filhos, na mesma ordem (maior primeiro), com datas preservadas (precisão de segundos).
        var originalNodes = original.Root.EnumerateDirectories().SelectMany(d => d.Children).Select(n => (n.FullPath, n.Size)).OrderBy(x => x.FullPath).ToList();
        var loadedNodes = loaded.Root.EnumerateDirectories().SelectMany(d => d.Children).Select(n => (n.FullPath, n.Size)).OrderBy(x => x.FullPath).ToList();
        Assert.Equal(originalNodes, loadedNodes);
        var photo = loaded.FindNode(temp.Combine("fotos", "praia.jpg"));
        Assert.NotNull(photo);
        Assert.InRange((photo!.LastModifiedUtc - original.FindNode(temp.Combine("fotos", "praia.jpg"))!.LastModifiedUtc).Duration(),
            TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(FileCategory.Image, photo.Category);
    }

    [Fact]
    public async Task Save_KeepsDeletionsMadeInTheApp()
    {
        using var temp = new TempDirectory();
        using var store = new TempDirectory();
        var result = await ScanSampleAsync(temp);
        result.RemoveNode(result.FindNode(temp.Combine("videos"))!);

        await ScanArchive.SaveAsync(result, store.Path);
        var loaded = await ScanArchive.LoadAsync(ScanArchive.PathFor(result.RootPath, store.Path));

        Assert.Null(loaded.FindNode(temp.Combine("videos")));
        Assert.Equal(result.TotalBytes, loaded.TotalBytes);
        Assert.DoesNotContain(loaded.LargestFiles, f => f.Name == "filme.mp4");
    }

    [Fact]
    public async Task InfoListFindAndDelete_WorkWithoutLoadingTheTree()
    {
        using var temp = new TempDirectory();
        using var store = new TempDirectory();
        var result = await ScanSampleAsync(temp);
        await ScanArchive.SaveAsync(result, store.Path);

        var info = Assert.Single(ScanArchive.List(store.Path));
        Assert.Equal(result.RootPath, info.RootPath);
        Assert.Equal(result.TotalBytes, info.TotalBytes);
        Assert.Equal(result.FileCount, info.FileCount);
        Assert.True(info.FileSizeBytes > 0);
        Assert.NotNull(ScanArchive.Find(result.RootPath, store.Path));

        ScanArchive.Delete(result.RootPath, store.Path);
        Assert.Null(ScanArchive.Find(result.RootPath, store.Path));
        Assert.Empty(ScanArchive.List(store.Path));
    }

    [Fact]
    public void PathFor_GivesShortNamesForDrivesAndDistinctNamesForFolders()
    {
        Assert.Equal("C.hrscan", Path.GetFileName(ScanArchive.PathFor(@"C:\", "x")));
        Assert.Equal("E.hrscan", Path.GetFileName(ScanArchive.PathFor("e:", "x")));
        Assert.NotEqual(ScanArchive.PathFor(@"D:\Projetos\a-b", "x"), ScanArchive.PathFor(@"D:\Projetos\a_b", "x"));
        Assert.Equal(ScanArchive.PathFor(@"D:\Projetos\App", "x"), ScanArchive.PathFor(@"d:\projetos\app\", "x"));
    }

    [Fact]
    public void ReadInfo_ReturnsNullForFilesThatAreNotAnalyses()
    {
        using var temp = new TempDirectory();
        string bogus = temp.CreateFileWithContent("falso.hrscan", "isto não é uma análise"u8.ToArray());
        Assert.Null(ScanArchive.ReadInfo(bogus));
    }
}
