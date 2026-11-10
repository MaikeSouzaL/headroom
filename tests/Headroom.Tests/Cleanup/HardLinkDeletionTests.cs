using Headroom.Core.Cleanup;
using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cleanup.Logging;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Cleanup.Safety;
using Headroom.Core.Settings;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Cleanup;

/// <summary>
/// Limpeza de pastas com hardlinks (uv, pnpm, conda): o "liberado" conta cada arquivo uma vez, como a análise, em
/// todos os caminhos da exclusão (pasta inteira para a Lixeira, item a item, arquivo indicado diretamente). As
/// pastas e os hardlinks são criados pelo teste em %TEMP% (NTFS); a Lixeira é falsa (move a pasta).
/// </summary>
public class HardLinkDeletionTests
{
    private sealed class MovingRecycler(string binPath) : IRecycler
    {
        public int FileCalls { get; private set; }
        public int DirectoryCalls { get; private set; }

        public void RecycleFile(string path)
        {
            FileCalls++;
            File.Move(path, Path.Combine(binPath, Guid.NewGuid().ToString("N")));
        }

        public void RecycleDirectory(string path)
        {
            DirectoryCalls++;
            Directory.Move(path, Path.Combine(binPath, Guid.NewGuid().ToString("N")));
        }
    }

    private static string Link(TempDirectory temp, string relativeLink, string existing)
    {
        string link = temp.Combine(relativeLink);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        TempDirectory.CreateHardLink(link, existing);
        return link;
    }

    /// <summary>archive com o arquivo (1000 bytes), dois ambientes ligando nele e um arquivo próprio (10 bytes).</summary>
    private static string BuildLinkedCache(TempDirectory temp)
    {
        string original = temp.CreateFile(@"alvo\cache\archive\pkg\mod.py", 1000);
        Link(temp, @"alvo\cache\env1\Lib\mod.py", original);
        Link(temp, @"alvo\cache\env2\Lib\mod.py", original);
        temp.CreateFile(@"alvo\cache\env2\own.txt", 10);
        return original;
    }

    private static DeletionOptions Options(TempDirectory temp, DeletionMode mode) => new()
    {
        Mode = mode,
        AllowedRoots = [new AllowedRoot(temp.Combine("alvo"))],
    };

    [Fact]
    public void RecycleBin_WholeFolder_CountsEachHardLinkedFileOnce()
    {
        using var temp = new TempDirectory();
        BuildLinkedCache(temp);
        var recycler = new MovingRecycler(temp.CreateDirectory("lixeira"));

        var result = new FileDeleter(recycler: recycler).Delete([new DeletionTarget(temp.Combine("alvo", "cache"), true)],
            Options(temp, DeletionMode.RecycleBin));

        Assert.Equal(1, recycler.DirectoryCalls);
        Assert.Equal(1010, result.BytesDeleted); // antes: 3010 (um por caminho)
        Assert.Equal(4, result.FilesDeleted); // caminhos continuam contados um a um
        Assert.False(Directory.Exists(temp.Combine("alvo", "cache")));
    }

    [Theory]
    [InlineData(DeletionMode.Permanent)]
    [InlineData(DeletionMode.RecycleBin)]
    public void SeveralTargets_LinksOfTheSameFileCountOnceAcrossTargets(DeletionMode mode)
    {
        using var temp = new TempDirectory();
        BuildLinkedCache(temp);
        var recycler = new MovingRecycler(temp.CreateDirectory("lixeira"));

        var result = new FileDeleter(recycler: recycler).Delete(
        [
            new DeletionTarget(temp.Combine("alvo", "cache", "archive"), true),
            new DeletionTarget(temp.Combine("alvo", "cache", "env1"), true),
            new DeletionTarget(temp.Combine("alvo", "cache", "env2"), true),
        ], Options(temp, mode));

        Assert.Equal(1010, result.BytesDeleted);
        Assert.Equal(4, result.FilesDeleted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Combine("alvo", "cache")));
    }

    [Theory]
    [InlineData(DeletionMode.Permanent)]
    [InlineData(DeletionMode.RecycleBin)]
    public void LooseFileTargets_LinksOfTheSameFileCountOnce(DeletionMode mode)
    {
        using var temp = new TempDirectory();
        string original = BuildLinkedCache(temp);
        var recycler = new MovingRecycler(temp.CreateDirectory("lixeira"));

        var result = new FileDeleter(recycler: recycler).Delete(
        [
            new DeletionTarget(original, false),
            new DeletionTarget(temp.Combine("alvo", "cache", "env1", "Lib", "mod.py"), false),
            new DeletionTarget(temp.Combine("alvo", "cache", "env2", "Lib", "mod.py"), false),
            new DeletionTarget(temp.Combine("alvo", "cache", "env2", "own.txt"), false),
        ], Options(temp, mode));

        Assert.Equal(1010, result.BytesDeleted);
        Assert.Equal(4, result.FilesDeleted);
    }

    [Theory]
    [InlineData(DeletionMode.Permanent)]
    [InlineData(DeletionMode.RecycleBin)]
    public void LooseFileThenFolderWithAnotherLink_FolderDoesNotCountItAgain(DeletionMode mode)
    {
        using var temp = new TempDirectory();
        string original = BuildLinkedCache(temp);
        var recycler = new MovingRecycler(temp.CreateDirectory("lixeira"));

        var result = new FileDeleter(recycler: recycler).Delete(
        [
            new DeletionTarget(original, false),
            new DeletionTarget(temp.Combine("alvo", "cache", "env2"), true),
        ], Options(temp, mode));

        Assert.Equal(1010, result.BytesDeleted);
        Assert.Equal(3, result.FilesDeleted);
    }

    [Fact]
    public void RecycleBin_FallbackAfterFileInUse_NeitherDoubleCountsNorLosesBytes()
    {
        using var temp = new TempDirectory();
        BuildLinkedCache(temp);
        string locked = temp.CreateFile(@"alvo\cache\env1\Scripts\python.exe", 77);
        var recycler = new MovingRecycler(temp.CreateDirectory("lixeira"));

        DeletionResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = new FileDeleter(recycler: recycler).Delete([new DeletionTarget(temp.Combine("alvo", "cache"), true)],
                Options(temp, DeletionMode.RecycleBin));
        }

        // A verificação da pasta inteira falhou (arquivo em uso) depois de já ter visto o arquivo ligado; o modo item
        // a item conta de novo do zero: 1000 do arquivo ligado + 10 do próprio, nada do que ficou.
        Assert.Equal(1, result.SkippedInUse);
        Assert.Equal(1010, result.BytesDeleted);
        Assert.True(File.Exists(locked));
        Assert.False(Directory.Exists(temp.Combine("alvo", "cache", "archive")));
        Assert.False(Directory.Exists(temp.Combine("alvo", "cache", "env2")));
        Assert.False(File.Exists(temp.Combine("alvo", "cache", "env1", "Lib", "mod.py")));
    }

    /// <summary>Muitas pastas (verificação em várias threads) com um link, um item protegido e um arquivo em uso no fundo.</summary>
    [Fact]
    public void RecycleBin_LargeTree_ParallelCheckKeepsEverySafetyRule()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        string precious = outside.CreateFile("precioso.docx", 4096);
        for (int a = 0; a < 12; a++)
        {
            for (int b = 0; b < 8; b++)
            {
                temp.CreateFile($@"alvo\cache\a{a}\b{b}\f.bin", 100);
                temp.CreateDirectory($@"alvo\cache\a{a}\b{b}\.tmpVazia");
            }
        }

        TempDirectory.CreateJunction(temp.Combine("alvo", "cache", "a3", "b5", "atalho"), outside.Path);
        string critical = temp.CreateFile(@"alvo\cache\a7\b1\pagefile.sys", 50);
        string locked = temp.CreateFile(@"alvo\cache\a10\b6\travado.bin", 30);
        var recycler = new MovingRecycler(temp.CreateDirectory("lixeira"));

        DeletionResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = new FileDeleter(recycler: recycler).Delete([new DeletionTarget(temp.Combine("alvo", "cache"), true)],
                Options(temp, DeletionMode.RecycleBin));
        }

        Assert.True(File.Exists(precious));
        Assert.Equal(4096, new FileInfo(precious).Length);
        Assert.True(File.Exists(critical));
        Assert.True(File.Exists(locked));
        Assert.Equal(1, result.SkippedLinks);
        Assert.Equal(1, result.SkippedProtected);
        Assert.Equal(1, result.SkippedInUse);
        Assert.Equal(0, result.Errors);
        Assert.Equal(12 * 8 * 100, result.BytesDeleted);
        Assert.Equal(12 * 8, result.FilesDeleted);
        Assert.Empty(Directory.EnumerateFiles(temp.Combine("alvo", "cache"), "f.bin", SearchOption.AllDirectories));
        // As pastas sem problema foram para a Lixeira inteiras; só os vizinhos dos 3 itens que ficaram, um a um.
        Assert.Equal(3, recycler.FileCalls);
    }

    [Fact]
    public void RecycleBin_CancelledBeforeStart_NothingMoves()
    {
        using var temp = new TempDirectory();
        BuildLinkedCache(temp);
        var recycler = new MovingRecycler(temp.CreateDirectory("lixeira"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = new FileDeleter(recycler: recycler).Delete([new DeletionTarget(temp.Combine("alvo", "cache"), true)],
            Options(temp, DeletionMode.RecycleBin), cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, recycler.DirectoryCalls);
        Assert.True(Directory.Exists(temp.Combine("alvo", "cache", "env1")));
    }

    [Fact]
    public void FolderSurvey_CancelledMidWay_IsNotClean()
    {
        using var temp = new TempDirectory();
        for (int i = 0; i < 40; i++)
        {
            temp.CreateFile($@"alvo\d{i}\f.bin", 10);
        }

        using var cts = new CancellationTokenSource();
        int checks = 0;
        string root = temp.Combine("alvo");
        var survey = FolderSurvey.Run(root, DirectoryReader.LocalNtfsVolume(root), _ =>
        {
            if (Interlocked.Increment(ref checks) == 5)
            {
                cts.Cancel();
            }

            return false;
        }, new HardLinkSet(), maxThreads: 4, cts.Token);

        Assert.False(survey.Clean);
    }

    [Fact]
    public void FolderSurvey_SameResultWithOneOrManyThreads()
    {
        using var temp = new TempDirectory();
        string original = temp.CreateFile(@"alvo\archive\x.py", 700);
        for (int i = 0; i < 30; i++)
        {
            Link(temp, $@"alvo\envs\e{i}\Lib\x.py", original);
            temp.CreateFile($@"alvo\envs\e{i}\own.bin", 10 + i);
        }

        string root = temp.Combine("alvo");
        uint? volume = DirectoryReader.LocalNtfsVolume(root);
        Assert.NotNull(volume);
        foreach (int threads in new[] { 1, 6 })
        {
            var survey = FolderSurvey.Run(root, volume, _ => false, new HardLinkSet(), threads, CancellationToken.None);
            Assert.True(survey.Clean);
            Assert.Equal(700 + Enumerable.Range(0, 30).Sum(i => 10 + i), survey.Bytes);
            Assert.Equal(1 + 30 * 2, survey.Files);
        }

        // Arquivo já contado antes (outro alvo desta exclusão): vale zero aqui.
        var counted = new HardLinkSet();
        var before = FolderSurvey.Run(temp.Combine("alvo", "archive"), volume, _ => false, new HardLinkSet(), 1, CancellationToken.None);
        counted.UnionWith(before.Seen);
        var rest = FolderSurvey.Run(temp.Combine("alvo", "envs"), volume, _ => false, counted, 4, CancellationToken.None);
        Assert.Equal(Enumerable.Range(0, 30).Sum(i => 10 + i), rest.Bytes);
    }

    [Fact]
    public async Task PathRule_RecycleBin_FreedEqualsAnalysisTotal()
    {
        using var temp = new TempDirectory();
        string keep = temp.CreateFile(@"projeto\.venv\Lib\mod.py", 4096);
        Link(temp, @"cacheA\env\mod.py", keep);
        Link(temp, @"cacheB\env\mod.py", keep);
        temp.CreateFile(@"cacheB\env\own.bin", 100);
        var paths = new PathExpander();
        paths.SetVariable("TESTROOT", temp.Path);
        var rule = new PathRule(new PathRuleDefinition
        {
            Id = "t.links",
            Name = "Teste",
            Targets = [new PathTarget { Path = @"%TESTROOT%\cacheA" }, new PathTarget { Path = @"%TESTROOT%\cacheB" }],
        });
        var recycler = new MovingRecycler(temp.CreateDirectory("lixeira"));

        var analysis = await rule.AnalyzeAsync(new CleanupContext(new AppSettings(), false, paths: paths), CancellationToken.None);
        var result = await rule.CleanAsync(analysis, new CleanupExecution(DeletionMode.RecycleBin, CleanupLog.Null(), false, recycler),
            null, CancellationToken.None);

        Assert.Equal(4096 + 100, analysis.TotalBytes);
        Assert.Equal(analysis.TotalBytes, result.BytesFreed); // antes: 2 × 4096 + 100
        Assert.True(File.Exists(keep));
    }

    [Theory]
    [InlineData(DeletionMode.Permanent)]
    [InlineData(DeletionMode.RecycleBin)]
    public async Task UvCacheRule_FreedEqualsAnalysisTotal(DeletionMode mode)
    {
        using var temp = new TempDirectory();
        string module = temp.CreateFile(@"cache\archive-v0\abc\pkg\mod.py", 1000);
        temp.CreateFile(@"cache\CACHEDIR.TAG", 43);
        for (int i = 0; i < 12; i++)
        {
            Link(temp, $@"cache\builds-v0\.tmp{i:D6}\Lib\site-packages\pkg\mod.py", module);
            temp.CreateFile($@"cache\builds-v0\.tmp{i:D6}\Scripts\python.exe", 300);
            temp.CreateDirectory($@"cache\builds-v0\.tmp{i:D6}\Lib\site-packages\pkg\.tmpAbCdEf");
        }

        var paths = new PathExpander();
        paths.SetVariable("TESTROOT", temp.Path);
        var rule = new UvCacheRule(new PathRuleDefinition
        {
            Id = "t.uv",
            Name = "uv: cache",
            Targets = [new PathTarget { Path = @"%TESTROOT%\cache" }],
        }, measuredEnvironments: 4);
        var recycler = new MovingRecycler(temp.CreateDirectory("lixeira"));

        var analysis = await rule.AnalyzeAsync(new CleanupContext(new AppSettings(), false, paths: paths), CancellationToken.None);
        var result = await rule.CleanAsync(analysis, new CleanupExecution(mode, CleanupLog.Null(), false, recycler),
            null, CancellationToken.None);

        Assert.Equal(1000 + 12 * 300 + 43, analysis.TotalBytes);
        Assert.Equal(analysis.TotalBytes, result.BytesFreed); // antes: 13 × 1000 + 12 × 300 + 43
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Combine("cache")));
    }

    [Fact]
    public void DeletionGuard_LinkCacheIsBounded_AndStillCatchesJunctions()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        outside.CreateFile("x.txt", 1);
        TempDirectory.CreateJunction(temp.Combine("j"), outside.Path);
        var guard = new DeletionGuard(ProtectedPaths.Default);
        var roots = new[] { new AllowedRoot(temp.Path) };

        Assert.True(guard.Check(temp.Combine("j", "x.txt"), roots).IsProtected);
        for (int i = 0; i < DeletionGuard.MaxCachedDirectories + 500; i++)
        {
            // Pastas que não existem: o guarda pergunta ao disco e guarda "não é link".
            Assert.False(guard.Check(temp.Combine($"d{i}", "f.bin"), roots).IsProtected);
        }

        Assert.InRange(guard.CachedDirectoryCount, 1, DeletionGuard.MaxCachedDirectories);
        Assert.True(guard.Check(temp.Combine("j", "x.txt"), roots).IsProtected);
    }
}
