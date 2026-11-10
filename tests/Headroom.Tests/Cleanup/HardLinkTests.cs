using Headroom.Core.Cleanup;
using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cleanup.Logging;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Settings;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Cleanup;

/// <summary>
/// Hardlinks somam o tamanho uma vez só (como o <c>du</c>): o uv, o pnpm e o conda ligam o mesmo arquivo em
/// centenas de pastas, e somar cada caminho chegava a mostrar 838 GB num disco de 931 GB. As pastas são criadas
/// em %TEMP% (NTFS), com hardlinks feitos pelo próprio teste.
/// </summary>
public class HardLinkTests
{
    private static string Link(TempDirectory temp, string relativeLink, string existing)
    {
        string link = temp.Combine(relativeLink);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        TempDirectory.CreateHardLink(link, existing);
        return link;
    }

    /// <summary>Um pacote em archive, ligado em dois ambientes, mais um arquivo próprio do segundo ambiente.</summary>
    private static void BuildLinkedTree(TempDirectory temp)
    {
        string original = temp.CreateFile(@"cache\archive\pkg\mod.py", 1000);
        Link(temp, @"cache\env1\Lib\mod.py", original);
        Link(temp, @"cache\env2\Lib\mod.py", original);
        temp.CreateFile(@"cache\env2\own.txt", 10);
    }

    private static long SumOfPaths(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    [Fact]
    public void Measure_CountsEachHardLinkedFileOnce()
    {
        using var temp = new TempDirectory();
        BuildLinkedTree(temp);
        string root = temp.Combine("cache");
        Assert.Equal(3010, SumOfPaths(root)); // o que a soma por caminho mostrava

        foreach (int threads in new[] { 1, 8 })
        {
            var (size, files, _) = ContentWalker.Measure(root, CancellationToken.None, threads);
            Assert.Equal(1010, size);
            Assert.Equal(4, files); // todos os caminhos continuam contados (são apagados um a um)
        }

        Assert.Equal(1010, PathRule.MeasureDirectory(root, CancellationToken.None).Size);
    }

    [Fact]
    public void Collect_GroupedFolders_TotalCountsLinkOnce()
    {
        using var temp = new TempDirectory();
        BuildLinkedTree(temp);

        var result = ContentWalker.Collect(temp.Combine("cache"), new PathTarget(), CancellationToken.None, 4);

        Assert.Equal(new[] { "archive", "env1", "env2" }, result.Items.Select(i => Path.GetFileName(i.Path)).Order());
        Assert.All(result.Items, i => Assert.True(i.IsDirectory));
        Assert.Equal(1010, result.Items.Sum(i => i.Size));
        Assert.Equal(1010, result.Size);
        // Quem fica com os 1000 bytes depende de qual pasta termina primeiro; a soma não.
        Assert.Single(result.Items, i => i.Size >= 1000);
    }

    [Fact]
    public void Collect_LooseFiles_OnlyOnePathCarriesTheSize()
    {
        using var temp = new TempDirectory();
        BuildLinkedTree(temp);
        // Um .txt em cada pasta impede o agrupamento: os .py viram itens soltos.
        temp.CreateFile(@"cache\archive\pkg\notes.txt", 5);
        temp.CreateFile(@"cache\env1\Lib\notes.txt", 5);
        temp.CreateFile(@"cache\env2\Lib\notes.txt", 5);

        var result = ContentWalker.Collect(temp.Combine("cache"), new PathTarget { Extensions = ["py"] }, CancellationToken.None, 4);

        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, i => Assert.EndsWith("mod.py", i.Path));
        Assert.Equal(1000, result.Items.Sum(i => i.Size));
        Assert.Equal(new[] { 0L, 0L, 1000L }, result.Items.Select(i => i.Size).Order());
    }

    [Fact]
    public async Task PathRule_LinksAcrossTargetsCountOnce_AndCleaningKeepsTheOtherLinkIntact()
    {
        using var temp = new TempDirectory();
        string keep = temp.CreateFile(@"projeto\.venv\Lib\mod.py", 4096);
        byte[] content = File.ReadAllBytes(keep);
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

        var analysis = await rule.AnalyzeAsync(new CleanupContext(new AppSettings(), false, paths: paths), CancellationToken.None);
        Assert.Equal(4096 + 100, analysis.TotalBytes);

        await rule.CleanAsync(analysis, new CleanupExecution(DeletionMode.Permanent, CleanupLog.Null(), false), null, CancellationToken.None);

        Assert.False(Directory.Exists(temp.Combine("cacheA", "env")));
        Assert.False(Directory.Exists(temp.Combine("cacheB", "env")));
        Assert.True(Directory.Exists(temp.Combine("cacheA")));
        // Apagar os outros caminhos só tira os nomes: o arquivo do projeto fica com o mesmo conteúdo.
        Assert.Equal(content, File.ReadAllBytes(keep));
    }

    [Fact]
    public void Measure_SkipDirectoriesNamed_OnlyForEstimates()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"env\Lib\pkg\mod.py", 100);
        temp.CreateDirectory(@"env\Lib\pkg\.tmpAbCdEf");
        temp.CreateFile(@"env\Lib\.tmpXyZ123\stray.bin", 7);

        Assert.Equal(107, ContentWalker.Measure(temp.Combine("env"), CancellationToken.None, 1).Size);
        var (size, files, _) = ContentWalker.Measure(temp.Combine("env"), CancellationToken.None, 1, skipDirectoriesNamed: ".tmp*");
        Assert.Equal(100, size);
        Assert.Equal(1, files);
    }

    [Fact]
    public void HardLinkSet_KeysByVolumeAndMftRecord()
    {
        var set = new HardLinkSet();
        Assert.True(set.TryAdd(1, 0x0001_0000_0000_1234));
        Assert.False(set.TryAdd(1, 0x0001_0000_0000_1234));
        Assert.False(set.TryAdd(1, 0x0002_0000_0000_1234)); // mesmo registro, outro número de sequência
        Assert.True(set.TryAdd(2, 0x0001_0000_0000_1234)); // outro volume
        Assert.True(set.TryAdd(1, 0x0001_0000_0010_0000)); // outra página
    }
}

/// <summary>
/// Regra do cache do uv: com poucos ambientes temporários é idêntica à regra de pasta; com milhares, mede uma
/// amostra de <c>builds-v0</c> e estima o resto, sem mudar o que é apagado.
/// </summary>
public class UvCacheRuleTests
{
    private const int EnvironmentCount = 12;

    /// <summary>Cache no formato do uv: archive-v0 com o pacote, ambientes em builds-v0 ligando nele.</summary>
    private static void BuildUvCache(TempDirectory temp, int environments)
    {
        string module = temp.CreateFile(@"cache\archive-v0\abc123\pkg\mod.py", 1000);
        temp.CreateFile(@"cache\archive-v0\abc123\pkg\other.py", 500);
        temp.CreateFile(@"cache\wheels-v6\pypi\pkg\pkg-1.0-py3-none-any.http", 40);
        temp.CreateFile(@"cache\simple-v21\pypi\pkg.rkyv", 60);
        temp.CreateFile(@"cache\CACHEDIR.TAG", 43);
        for (int i = 0; i < environments; i++)
        {
            string env = $@"cache\builds-v0\.tmp{i:D6}";
            string link = temp.Combine(env, "Lib", "site-packages", "pkg", "mod.py");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            TempDirectory.CreateHardLink(link, module);
            temp.CreateFile(Path.Combine(env, "Scripts", "python.exe"), 300);
            temp.CreateDirectory(Path.Combine(env, "Lib", "site-packages", "pkg", ".tmpAbCdEf")); // sobra vazia do uv
        }
    }

    private static (UvCacheRule Rule, CleanupContext Context) CreateRule(TempDirectory temp, int measuredEnvironments)
    {
        var paths = new PathExpander();
        paths.SetVariable("TESTROOT", temp.Path);
        var definition = new PathRuleDefinition
        {
            Id = "t.uv",
            Name = "uv: cache",
            Targets = [new PathTarget { Path = @"%TESTROOT%\cache" }],
        };
        return (new UvCacheRule(definition, measuredEnvironments), new CleanupContext(new AppSettings(), false, paths: paths));
    }

    [Fact]
    public async Task FewEnvironments_MeasuredExactly_SameItemsAsPlainPathRule()
    {
        using var temp = new TempDirectory();
        BuildUvCache(temp, environments: 3);
        var (rule, context) = CreateRule(temp, measuredEnvironments: 4);
        var plain = new PathRule(rule.Definition);

        var expected = await plain.AnalyzeAsync(context, CancellationToken.None);
        var actual = await rule.AnalyzeAsync(context, CancellationToken.None);

        // Mesmos itens, mesmos arquivos e mesmo total. No percurso comum, qual pasta fica com o arquivo ligado
        // depende de qual termina primeiro; na regra do uv é sempre archive-v0.
        Assert.Equal(expected.Items.Select(i => (i.Path, i.IsDirectory, i.FileCount)).Order(),
            actual.Items.Select(i => (i.Path, i.IsDirectory, i.FileCount)).Order());
        Assert.Equal(expected.TotalBytes, actual.TotalBytes);
        Assert.All(actual.Items, i => Assert.Null(i.Reason));
        Assert.Equal(1500, Assert.Single(actual.Items, i => Path.GetFileName(i.Path) == "archive-v0").Size);
        Assert.Equal(3 * 300, Assert.Single(actual.Items, i => Path.GetFileName(i.Path) == UvCacheRule.BuildsBucket).Size);
        // 1500 do archive (o link em cada ambiente conta uma vez) + 3 × 300 + 40 + 60 + 43.
        Assert.Equal(1500 + 3 * 300 + 40 + 60 + 43, actual.TotalBytes);
    }

    [Fact]
    public async Task ManyEnvironments_BuildsIsOneEstimatedItem_RestIsExact()
    {
        using var temp = new TempDirectory();
        BuildUvCache(temp, EnvironmentCount);
        var (rule, context) = CreateRule(temp, measuredEnvironments: 4);

        var analysis = await rule.AnalyzeAsync(context, CancellationToken.None);

        var builds = Assert.Single(analysis.Items, i => Path.GetFileName(i.Path) == UvCacheRule.BuildsBucket);
        Assert.True(builds.IsDirectory);
        Assert.Equal(temp.Combine("cache", "builds-v0"), builds.Path);
        // Os links para archive-v0 contam zero (archive é medido antes); cada ambiente tem 300 bytes próprios.
        Assert.Equal(EnvironmentCount * 300, builds.Size);
        Assert.Equal(EnvironmentCount * 2, builds.FileCount);
        Assert.Contains("12 ambientes temporários", builds.Reason);
        Assert.Contains("estimado medindo 4", builds.Reason);

        var archive = Assert.Single(analysis.Items, i => Path.GetFileName(i.Path) == "archive-v0");
        Assert.Equal(1500, archive.Size);
        Assert.Equal(1500 + EnvironmentCount * 300 + 40 + 60 + 43, analysis.TotalBytes);
        Assert.All(analysis.Items, i => Assert.StartsWith(temp.Combine("cache") + Path.DirectorySeparatorChar, i.Path));
        // Nenhum item de dentro de builds-v0: a pasta não foi descida no percurso exato.
        Assert.DoesNotContain(analysis.Items, i => i.Path.Contains(@"\builds-v0\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ManyEnvironments_CleaningRemovesBuildsButNeverFollowsJunctions()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        string important = outside.CreateFile("importante.docx", 999);
        BuildUvCache(temp, EnvironmentCount);
        TempDirectory.CreateJunction(temp.Combine("cache", "builds-v0", ".tmp000003", "link"), outside.Path);
        var (rule, context) = CreateRule(temp, measuredEnvironments: 4);

        var analysis = await rule.AnalyzeAsync(context, CancellationToken.None);
        var result = await rule.CleanAsync(analysis, new CleanupExecution(DeletionMode.Permanent, CleanupLog.Null(), false),
            null, CancellationToken.None);

        Assert.False(Directory.Exists(temp.Combine("cache", "builds-v0", ".tmp000000")));
        Assert.False(Directory.Exists(temp.Combine("cache", "archive-v0")));
        Assert.True(Directory.Exists(temp.Combine("cache")));
        Assert.True(File.Exists(important));
        Assert.Equal(999, new FileInfo(important).Length);
        Assert.Equal(0, result.Deletion!.Errors);
    }

    [Fact]
    public async Task BuildsBucketThatIsAJunction_IsNeverEnteredNorCleaned()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        for (int i = 0; i < EnvironmentCount; i++)
        {
            outside.CreateFile($@".tmp{i:D6}\importante.bin", 500);
        }

        temp.CreateFile(@"cache\archive-v0\abc123\pkg\mod.py", 1000);
        TempDirectory.CreateJunction(temp.Combine("cache", UvCacheRule.BuildsBucket), outside.Path);
        var (rule, context) = CreateRule(temp, measuredEnvironments: 4);

        var analysis = await rule.AnalyzeAsync(context, CancellationToken.None);
        Assert.Equal(1000, analysis.TotalBytes);
        Assert.DoesNotContain(analysis.Items, i => i.Path.Contains(UvCacheRule.BuildsBucket, StringComparison.OrdinalIgnoreCase));

        await rule.CleanAsync(analysis, new CleanupExecution(DeletionMode.Permanent, CleanupLog.Null(), false), null, CancellationToken.None);
        for (int i = 0; i < EnvironmentCount; i++)
        {
            Assert.True(File.Exists(outside.Combine($".tmp{i:D6}", "importante.bin")));
        }
    }

    [Fact]
    public async Task ManyEnvironments_CancelledAnalysisIsCancellationNotError()
    {
        using var temp = new TempDirectory();
        BuildUvCache(temp, EnvironmentCount);
        var (rule, context) = CreateRule(temp, measuredEnvironments: 4);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rule.AnalyzeAsync(context, cts.Token));
    }

    [Fact]
    public void Catalog_UsesUvCacheRuleForDevUv()
    {
        var rule = Assert.Single(Core.Cleanup.Catalog.RuleCatalog.BuiltIn(new AppSettings()), r => r.Id == "dev.uv");
        var uv = Assert.IsType<UvCacheRule>(rule);
        Assert.Contains("uv.exe", uv.ProcessesToClose);
        var target = Assert.Single(uv.Definition.Targets);
        Assert.Equal(@"%LOCALAPPDATA%\uv\cache", target.Path);
        Assert.Equal(TargetMode.Contents, target.Mode);
    }
}
