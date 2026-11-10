using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cleanup.Safety;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Cleanup;

public class FileDeleterTests
{
    /// <summary>"Lixeira" falsa: move para uma pasta temporária, sem tocar na Lixeira real.</summary>
    private sealed class FakeRecycler(string binPath) : IRecycler
    {
        public int FileCalls { get; private set; }
        public int DirectoryCalls { get; private set; }

        public void RecycleFile(string path)
        {
            FileCalls++;
            File.Move(path, Path.Combine(binPath, Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(path)));
        }

        public void RecycleDirectory(string path)
        {
            DirectoryCalls++;
            Directory.Move(path, Path.Combine(binPath, Guid.NewGuid().ToString("N")));
        }
    }

    private static DeletionOptions Options(TempDirectory temp, DeletionMode mode) => new()
    {
        Mode = mode,
        AllowedRoots = new[] { new AllowedRoot(temp.Combine("alvo")) },
    };

    [Fact]
    public void Permanent_DeletesFilesAndCountsBytes()
    {
        using var temp = new TempDirectory();
        string a = temp.CreateFile(@"alvo\a.tmp", 1000);
        temp.CreateFile(@"alvo\sub\b.tmp", 2000);

        var result = new FileDeleter().Delete(new[]
        {
            new DeletionTarget(a, false),
            new DeletionTarget(temp.Combine("alvo", "sub"), true),
        }, Options(temp, DeletionMode.Permanent));

        Assert.Equal(3000, result.BytesDeleted);
        Assert.Equal(2, result.FilesDeleted);
        Assert.False(File.Exists(a));
        Assert.False(Directory.Exists(temp.Combine("alvo", "sub")));
        Assert.True(Directory.Exists(temp.Combine("alvo")));
    }

    [Fact]
    public void FilesInUse_AreSkippedWithoutBlocking()
    {
        using var temp = new TempDirectory();
        string locked = temp.CreateFile(@"alvo\pasta\travado.log", 500);
        string free = temp.CreateFile(@"alvo\pasta\livre.log", 700);

        DeletionResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = new FileDeleter().Delete(new[] { new DeletionTarget(temp.Combine("alvo", "pasta"), true) },
                Options(temp, DeletionMode.Permanent));
        }

        Assert.Equal(1, result.SkippedInUse);
        Assert.Equal(700, result.BytesDeleted);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
    }

    [Fact]
    public void RecycleBin_SendsCleanFolderInOneOperation()
    {
        using var temp = new TempDirectory();
        string bin = temp.CreateDirectory("lixeira");
        temp.CreateFile(@"alvo\cache\1.bin", 100);
        temp.CreateFile(@"alvo\cache\x\2.bin", 200);
        var recycler = new FakeRecycler(bin);

        var result = new FileDeleter(recycler: recycler).Delete(
            new[] { new DeletionTarget(temp.Combine("alvo", "cache"), true) }, Options(temp, DeletionMode.RecycleBin));

        Assert.Equal(1, recycler.DirectoryCalls);
        Assert.Equal(0, recycler.FileCalls);
        Assert.Equal(300, result.BytesDeleted);
        Assert.Equal(2, result.FilesDeleted);
        Assert.False(Directory.Exists(temp.Combine("alvo", "cache")));
    }

    [Fact]
    public void RecycleBin_FallsBackToItemByItemWhenSomethingIsLocked()
    {
        using var temp = new TempDirectory();
        string bin = temp.CreateDirectory("lixeira");
        string locked = temp.CreateFile(@"alvo\cache\travado.bin", 100);
        temp.CreateFile(@"alvo\cache\ok1.bin", 200);
        temp.CreateFile(@"alvo\cache\sub\ok2.bin", 300);
        var recycler = new FakeRecycler(bin);

        DeletionResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = new FileDeleter(recycler: recycler).Delete(
                new[] { new DeletionTarget(temp.Combine("alvo", "cache"), true) }, Options(temp, DeletionMode.RecycleBin));
        }

        Assert.Equal(1, result.SkippedInUse);
        Assert.Equal(500, result.BytesDeleted);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(temp.Combine("alvo", "cache", "ok1.bin")));
        Assert.False(Directory.Exists(temp.Combine("alvo", "cache", "sub")));
    }

    [Fact]
    public void NeverFollowsJunctionsWhenDeleting()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        string precious = outside.CreateFile("precioso.docx", 4096);
        temp.CreateFile(@"alvo\lixo\a.tmp", 10);
        TempDirectory.CreateJunction(temp.Combine("alvo", "lixo", "atalho"), outside.Path);

        var result = new FileDeleter().Delete(new[] { new DeletionTarget(temp.Combine("alvo", "lixo"), true) },
            Options(temp, DeletionMode.Permanent));

        Assert.True(File.Exists(precious));
        Assert.Equal(1, result.SkippedLinks);
        Assert.Equal(10, result.BytesDeleted);
    }

    [Fact]
    public void ItemsOutsideAllowedRootsAreNeverDeleted()
    {
        using var temp = new TempDirectory();
        string outsideFile = temp.CreateFile(@"fora\importante.txt", 50);
        string system32File = Path.Combine(Environment.SystemDirectory, "kernel32.dll");

        var result = new FileDeleter().Delete(new[]
        {
            new DeletionTarget(outsideFile, false),
            new DeletionTarget(system32File, false),
            new DeletionTarget(temp.Combine("alvo"), true),
        }, Options(temp, DeletionMode.Permanent));

        Assert.True(File.Exists(outsideFile));
        Assert.True(File.Exists(system32File));
        Assert.Equal(3, result.SkippedProtected);
        Assert.Equal(0, result.BytesDeleted);
    }

    [Fact]
    public void Permanent_RemovesReadOnlyFiles()
    {
        using var temp = new TempDirectory();
        string file = temp.CreateFile(@"alvo\somente-leitura.txt", 30);
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var result = new FileDeleter().Delete(new[] { new DeletionTarget(file, false) }, Options(temp, DeletionMode.Permanent));

        Assert.False(File.Exists(file));
        Assert.Equal(30, result.BytesDeleted);
    }

    [Fact]
    public void Cancellation_StopsBeforeRemainingTargets()
    {
        using var temp = new TempDirectory();
        string a = temp.CreateFile(@"alvo\a.tmp", 10);
        string b = temp.CreateFile(@"alvo\b.tmp", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = new FileDeleter().Delete(new[] { new DeletionTarget(a, false), new DeletionTarget(b, false) },
            Options(temp, DeletionMode.Permanent), cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void MissingItems_AreCountedAsNotFound()
    {
        using var temp = new TempDirectory();
        var result = new FileDeleter().Delete(new[] { new DeletionTarget(temp.Combine("alvo", "sumiu.tmp"), false) },
            Options(temp, DeletionMode.Permanent));

        Assert.Equal(1, result.NotFound);
    }

    [Fact]
    public async Task DeleteAsync_RunsOnStaThreadAndReturnsResult()
    {
        using var temp = new TempDirectory();
        string a = temp.CreateFile(@"alvo\a.tmp", 10);

        var result = await new FileDeleter().DeleteAsync(new[] { new DeletionTarget(a, false) }, Options(temp, DeletionMode.Permanent));

        Assert.Equal(10, result.BytesDeleted);
    }
}
