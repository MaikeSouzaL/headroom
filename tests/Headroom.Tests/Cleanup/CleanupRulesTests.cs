using Headroom.Core.Cleanup;
using Headroom.Core.Cleanup.Catalog;
using Headroom.Core.Cleanup.Declarative;
using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Cleanup.Safety;
using Headroom.Core.Settings;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Cleanup;

public class CleanupRulesTests
{
    private static CleanupContext Context(TempDirectory temp)
    {
        var paths = new PathExpander();
        paths.SetVariable("TESTROOT", temp.Path);
        return new CleanupContext(new AppSettings(), isAdministrator: false, paths: paths);
    }

    private static CleanupExecution Execution(TempDirectory temp) =>
        new(DeletionMode.Permanent, Core.Cleanup.Logging.CleanupLog.Null(), false);

    [Fact]
    public async Task PathRule_AppliesAgeFilterAndGroupsWholeFolders()
    {
        using var temp = new TempDirectory();
        var old = DateTime.UtcNow.AddDays(-10);
        temp.CreateFile(@"cache\velho.tmp", 100, lastWriteUtc: old);
        temp.CreateFile(@"cache\novo.tmp", 200);
        temp.CreateFile(@"cache\subvelha\a.tmp", 300, lastWriteUtc: old);
        temp.CreateFile(@"cache\subvelha\b.tmp", 400, lastWriteUtc: old);
        temp.CreateFile(@"cache\submista\c.tmp", 500, lastWriteUtc: old);
        temp.CreateFile(@"cache\submista\d.tmp", 600);

        var rule = RuleBuilder.Def("t.age", CleanupCategory.System, "Teste", "Teste")
            .Contents(@"%TESTROOT%\cache", minAgeDays: 5).Build();
        var analysis = await rule.AnalyzeAsync(Context(temp), CancellationToken.None);

        var paths = analysis.Items.Select(i => Path.GetRelativePath(temp.Path, i.Path)).OrderBy(p => p).ToList();
        Assert.Equal(new[] { @"cache\submista\c.tmp", @"cache\subvelha", @"cache\velho.tmp" }, paths);
        Assert.Equal(100 + 700 + 500, analysis.TotalBytes);
        Assert.True(analysis.Items.Single(i => i.Path.EndsWith("subvelha")).IsDirectory);
    }

    [Fact]
    public async Task PathRule_ExtensionPatternAndExclusionFilters()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"logs\a.log", 10);
        temp.CreateFile(@"logs\b.txt", 20);
        temp.CreateFile(@"logs\keep.log", 30);
        temp.CreateFile(@"logs\thumbcache_1.db", 40);

        var byExt = await RuleBuilder.Def("t.ext", CleanupCategory.System, "T", "T")
            .Contents(@"%TESTROOT%\logs", extensions: [".log"], exclude: ["keep.*"]).Build()
            .AnalyzeAsync(Context(temp), CancellationToken.None);
        Assert.Equal(new[] { "a.log" }, byExt.Items.Select(i => i.Name));

        var byPattern = await RuleBuilder.Def("t.pat", CleanupCategory.System, "T", "T")
            .Contents(@"%TESTROOT%\logs", pattern: "thumbcache_*.db", recursive: false).Build()
            .AnalyzeAsync(Context(temp), CancellationToken.None);
        Assert.Equal(new[] { "thumbcache_1.db" }, byPattern.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task PathRule_SelfAndChildrenModesWithWildcards()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"Profiles\Default\Cache\x.bin", 10);
        temp.CreateFile(@"Profiles\Profile 1\Cache\y.bin", 20);
        temp.CreateFile(@"backups\b1\data", 100);
        temp.CreateFile(@"backups\b2\data", 200);

        var self = await RuleBuilder.Def("t.self", CleanupCategory.System, "T", "T")
            .Self(@"%TESTROOT%\Profiles\*\Cache").Build().AnalyzeAsync(Context(temp), CancellationToken.None);
        Assert.Equal(2, self.Items.Count);
        Assert.All(self.Items, i => Assert.True(i.IsDirectory));

        var children = await RuleBuilder.Def("t.children", CleanupCategory.System, "T", "T")
            .Children(@"%TESTROOT%\backups").Unselected().Build().AnalyzeAsync(Context(temp), CancellationToken.None);
        Assert.Equal(new long[] { 200, 100 }, children.Items.Select(i => i.Size));
        Assert.All(children.Items, i => Assert.False(i.IsSelected));
    }

    [Fact]
    public async Task PathRule_CleanDeletesOnlySelectedItemsInsideTarget()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"cache\a.tmp", 100);
        temp.CreateFile(@"cache\b.tmp", 200);
        string outside = temp.CreateFile(@"fora\c.tmp", 300);

        var rule = RuleBuilder.Def("t.clean", CleanupCategory.System, "T", "T").Contents(@"%TESTROOT%\cache").Build();
        var analysis = await rule.AnalyzeAsync(Context(temp), CancellationToken.None);
        analysis.Items.Single(i => i.Name == "b.tmp").IsSelected = false;

        var result = await rule.CleanAsync(analysis, Execution(temp), null, CancellationToken.None);

        Assert.Equal(100, result.BytesFreed);
        Assert.False(File.Exists(temp.Combine("cache", "a.tmp")));
        Assert.True(File.Exists(temp.Combine("cache", "b.tmp")));
        Assert.True(File.Exists(outside));
        Assert.True(Directory.Exists(temp.Combine("cache")));
    }

    [Fact]
    public async Task PathRule_NeverListsJunctionContents()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        outside.CreateFile("importante.docx", 999);
        temp.CreateFile(@"cache\a.tmp", 1);
        TempDirectory.CreateJunction(temp.Combine("cache", "link"), outside.Path);

        var rule = RuleBuilder.Def("t.junction", CleanupCategory.System, "T", "T").Contents(@"%TESTROOT%\cache").Build();
        var analysis = await rule.AnalyzeAsync(Context(temp), CancellationToken.None);
        await rule.CleanAsync(analysis, Execution(temp), null, CancellationToken.None);

        Assert.DoesNotContain(analysis.Items, i => i.Path.Contains("link"));
        Assert.True(File.Exists(outside.Combine("importante.docx")));
    }

    [Fact]
    public void PathExpander_ExpandsEnvironmentCustomVariablesAndWildcards()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(@"lib1\steamapps\shadercache");
        temp.CreateDirectory(@"lib2\steamapps\shadercache");
        temp.CreateDirectory(@"x\a1\Cache");
        temp.CreateDirectory(@"x\b2\Cache");
        temp.CreateDirectory(@"x\b2\Outro");

        var paths = new PathExpander();
        paths.SetVariable("LIBS", temp.Combine("lib1"), temp.Combine("lib2"), temp.Combine("nao-existe"));
        paths.SetVariable("TESTROOT", temp.Path);

        Assert.Equal(2, paths.Expand(@"%LIBS%\steamapps\shadercache").Count);
        Assert.Equal(2, paths.Expand(@"%TESTROOT%\x\*\Cache").Count);
        Assert.Single(paths.Expand(@"%TESTROOT%\x\b?\Outro"));
        Assert.Empty(paths.Expand(@"%VARIAVEL_INEXISTENTE_123%\x"));
        Assert.Contains(paths.Expand("%TEMP%"), p => Directory.Exists(p));
    }

    [Fact]
    public void JsonRuleLoader_ParsesValidRulesAndReportsErrors()
    {
        const string json = """
        [
          {
            // comentários são permitidos
            "id": "meuapp.cache",
            "name": "Meu App: cache",
            "description": "Cache do Meu App.",
            "category": "Applications",
            "risk": "Safe",
            "processesToClose": ["meuapp.exe"],
            "targets": [ { "path": "%LOCALAPPDATA%\\MeuApp\\Cache", "minAgeDays": 3, "extensions": [".tmp"] } ]
          },
          { "id": "", "name": "Sem id", "targets": [ { "path": "C:\\x" } ] },
          { "id": "relativo", "name": "Relativo", "targets": [ { "path": "pasta\\relativa" } ] },
          { "id": "pontos", "name": "Pontos", "targets": [ { "path": "C:\\Temp\\..\\Windows" } ] }
        ]
        """;

        var (rules, errors) = JsonRuleLoader.Parse(json, "teste.json");

        var rule = Assert.IsType<PathRule>(Assert.Single(rules));
        Assert.Equal("meuapp.cache", rule.Id);
        Assert.Equal(CleanupCategory.Applications, rule.Category);
        Assert.Equal(new[] { "meuapp.exe" }, rule.ProcessesToClose);
        Assert.Equal(3, rule.Definition.Targets[0].MinAgeDays);
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void JsonRuleLoader_RejectsInvalidJson()
    {
        var (rules, errors) = JsonRuleLoader.Parse("{ isto não é json", "ruim.json");
        Assert.Empty(rules);
        Assert.Contains("JSON inválido", Assert.Single(errors).Message);
    }

    [Fact]
    public void BuiltInCatalog_HasUniqueIdsAndDescriptions()
    {
        var rules = RuleCatalog.BuiltIn(new AppSettings());
        Assert.True(rules.Count > 80, $"Esperava um catálogo amplo, veio {rules.Count}.");
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.False(string.IsNullOrWhiteSpace(r.Description));
        });
        Assert.All(rules.Where(r => r.Category == CleanupCategory.UserFiles), r =>
        {
            Assert.Equal(RiskLevel.Review, r.Risk);
            Assert.False(r.ItemsSelectedByDefault);
        });
    }

    [Fact]
    public void BuiltInPathRules_NeverTargetProtectedLocations()
    {
        var paths = new PathExpander();
        foreach (var rule in RuleCatalog.BuiltIn(new AppSettings()).OfType<PathRule>())
        {
            foreach (var target in rule.Definition.Targets)
            {
                foreach (var resolved in paths.Expand(target.Path))
                {
                    string sample = target.Mode == TargetMode.Self ? resolved : Path.Combine(resolved, "arquivo-exemplo.tmp");
                    var check = ProtectedPaths.Default.Check(sample, rule.AllowBrowserPrivacyData);
                    Assert.False(check.IsProtected, $"Regra {rule.Id} aponta para local protegido: {sample} ({check.Reason})");
                }
            }
        }
    }

    [Fact]
    public void Parsers_HandleVersionFolderNames()
    {
        Assert.Equal(("ms-python.python", new Version(2024, 2, 1)), OldVersionsRule.ParseVsCodeExtension("ms-python.python-2024.2.1"));
        Assert.Equal("ms-dotnettools.csharp|win32-x64", OldVersionsRule.ParseVsCodeExtension("ms-dotnettools.csharp-2.1.2-win32-x64")!.Value.Item1);
        Assert.Equal(("gradle-bin", new Version(8, 5)), OldVersionsRule.ParseGradleDist("gradle-8.5-bin"));
        Assert.Equal(("IntelliJIdea", new Version(2023, 2)), OldVersionsRule.ParseJetBrains("IntelliJIdea2023.2"));
        Assert.Null(OldVersionsRule.ParseJetBrains("consentOptions"));
        Assert.Equal(new Version(34, 0), OldVersionsRule.ParseAndroidVersion("android-34")!.Value.Item2);
    }

    [Fact]
    public async Task OldVersionsRule_KeepsNewestPerKey()
    {
        using var temp = new TempDirectory();
        foreach (var name in new[] { "pub.ext-1.0.0", "pub.ext-1.2.0", "pub.ext-1.10.0", "outro.ext-0.1.0" })
        {
            temp.CreateFile($@"ext\{name}\package.json", 10);
        }

        var rule = new OldVersionsRule("t.old", CleanupCategory.Developer, "T", "T", RiskLevel.Safe,
            @"%TESTROOT%\ext", OldVersionsRule.ParseVsCodeExtension);
        var analysis = await rule.AnalyzeAsync(Context(temp), CancellationToken.None);

        Assert.Equal(new[] { "pub.ext-1.0.0", "pub.ext-1.2.0" }, analysis.Items.Select(i => i.Name).OrderBy(n => n));
    }

    [Fact]
    public void StaleProjects_ProjectDateIgnoresGeneratedFolders()
    {
        using var temp = new TempDirectory();
        var old = DateTime.UtcNow.AddDays(-400);
        temp.CreateFile(@"proj\package.json", 10, lastWriteUtc: old);
        temp.CreateFile(@"proj\src\index.js", 10, lastWriteUtc: old);
        temp.CreateFile(@"proj\node_modules\pkg\novo.js", 10); // recente, mas deve ser ignorado

        var date = StaleProjectsRule.ProjectLastModified(temp.Combine("proj"), CancellationToken.None);

        Assert.True(date < DateTime.Now.AddDays(-300));
    }

    [Fact]
    public void Helpers_ParseDockerSizesZipCoverageAndWorkspaceTargets()
    {
        Assert.Equal(2_500_000_000, DevCatalog.ParseDockerSize("2.5GB (50%)"));
        Assert.Equal(512_000, DevCatalog.ParseDockerSize("512kB"));

        using var temp = new TempDirectory();
        string zip = temp.Combine("pacote.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.CreateEntry("pacote/a.txt");
            archive.CreateEntry("pacote/b.txt");
        }

        temp.CreateFile(@"pacote\a.txt", 1);
        Assert.Equal(0.5, ExtractedArchivesRule.ZipCoverage(zip, temp.Combine("pacote")), 3);
        temp.CreateFile(@"pacote\b.txt", 1);
        Assert.Equal(1.0, ExtractedArchivesRule.ZipCoverage(zip, temp.Combine("pacote")), 3);

        string ws = temp.CreateFileWithContent("workspace.json", System.Text.Encoding.UTF8.GetBytes("""{"folder":"file:///c%3A/Projetos/Meu%20App"}"""));
        Assert.Equal(@"c:\Projetos\Meu App", VsCodeWorkspaceStorageRule.ReadWorkspaceTarget(ws));
        string remote = temp.CreateFileWithContent("remote.json", System.Text.Encoding.UTF8.GetBytes("""{"folder":"vscode-remote://wsl+Ubuntu/home/x"}"""));
        Assert.Null(VsCodeWorkspaceStorageRule.ReadWorkspaceTarget(remote));
    }
}
