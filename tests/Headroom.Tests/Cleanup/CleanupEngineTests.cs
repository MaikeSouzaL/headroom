using System.Diagnostics;
using Headroom.Core.Cleanup;
using Headroom.Core.Cleanup.Catalog;
using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Settings;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Cleanup;

public class CleanupEngineTests
{
    /// <summary>Regra falsa que só registra a ordem em que foi limpa.</summary>
    private sealed class RecordingRule(string id, RiskLevel risk, List<string> log)
        : CleanupRuleBase(id, CleanupCategory.System, id, id, risk)
    {
        public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
            Task.FromResult(CleanupAnalysis.Action(this, 10, "teste"));

        public override Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
            IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
        {
            log.Add(Id);
            return Task.FromResult(new CleanupRunResult(this) { BytesFreed = 10 });
        }
    }

    private static CleanupEngine Engine() => new(new CleanupContext(new AppSettings(), false));

    [Fact]
    public async Task Clean_RunsRecycleBinFirstThenSafeReviewAdvanced()
    {
        using var logs = new TempDirectory();
        var order = new List<string>();
        var rules = new ICleanupRule[]
        {
            new RecordingRule("avancada", RiskLevel.Advanced, order),
            new RecordingRule("revisar", RiskLevel.Review, order),
            new RecordingRule("segura", RiskLevel.Safe, order),
            new RecordingRule(RecycleBinRule.RuleId, RiskLevel.Safe, order),
        };

        var engine = Engine();
        var analyses = await engine.AnalyzeAsync(rules);
        var summary = await engine.CleanAsync(analyses, new CleanupRunOptions { CreateRestorePoint = false, LogDirectory = logs.Path });

        Assert.Equal(new[] { RecycleBinRule.RuleId, "segura", "revisar", "avancada" }, order);
        Assert.Equal(40, summary.TotalBytesFreed);
        Assert.True(File.Exists(summary.LogPath));
    }

    [Fact]
    public async Task Clean_SkipsRulesWhoseRequiredProcessIsOpenInSilentMode()
    {
        using var logs = new TempDirectory();
        var order = new List<string>();
        string current = Process.GetCurrentProcess().ProcessName + ".exe";
        var rule = new ProcessRule("navegador", order, current);

        var engine = Engine();
        var analyses = await engine.AnalyzeAsync(new[] { rule });
        var summary = await engine.CleanAsync(analyses, new CleanupRunOptions { CreateRestorePoint = false, LogDirectory = logs.Path });

        Assert.Empty(order);
        Assert.Single(summary.SkippedRules);
    }

    [Fact]
    public async Task Analyze_TurnsExceptionsIntoUnavailableResults()
    {
        var engine = Engine();
        var analyses = await engine.AnalyzeAsync(new ICleanupRule[] { new ThrowingRule() });

        Assert.True(analyses[0].IsUnavailable);
        Assert.Contains("Erro na análise", analyses[0].Note);
    }

    [Fact]
    public async Task EndToEnd_PathRuleThroughEngineWithPermanentMode()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"cache\a.tmp", 1000);
        temp.CreateFile(@"cache\sub\b.tmp", 2000);
        var paths = new PathExpander();
        paths.SetVariable("TESTROOT", temp.Path);
        var engine = new CleanupEngine(new CleanupContext(new AppSettings(), false, paths: paths));
        var rule = RuleBuilder.Def("t.e2e", CleanupCategory.Applications, "E2E", "E2E").Contents(@"%TESTROOT%\cache").Build();

        var analyses = await engine.AnalyzeAsync(new[] { rule });
        var summary = await engine.CleanAsync(analyses,
            new CleanupRunOptions { Mode = DeletionMode.Permanent, CreateRestorePoint = false, LogDirectory = temp.Combine("logs") });

        Assert.Equal(3000, summary.TotalBytesFreed);
        Assert.Equal(3000, summary.BytesByCategory[CleanupCategory.Applications]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Combine("cache")));
        string log = File.ReadAllText(summary.LogPath!);
        Assert.Contains("a.tmp", log);
        Assert.Contains("RESUMO", log);
    }

    [Fact]
    public void Profiles_SelectExpectedRules()
    {
        var rules = RuleCatalog.BuiltIn(new AppSettings());
        var quick = CleanupProfiles.Find(CleanupProfiles.Quick, Array.Empty<string>());
        var dev = CleanupProfiles.Find(CleanupProfiles.Developer, Array.Empty<string>());
        var custom = CleanupProfiles.Find(CleanupProfiles.Custom, new[] { "sys.temp.user" });

        Assert.All(rules.Where(quick.Includes), r => Assert.Equal(RiskLevel.Safe, r.Risk));
        Assert.Contains(rules, r => r.Id == "sys.temp.user" && quick.Includes(r));
        Assert.DoesNotContain(rules, r => r.Category == CleanupCategory.Advanced && quick.Includes(r));
        Assert.Contains(rules, r => r.Id == "dev.npm" && dev.Includes(r));
        Assert.Single(rules, custom.Includes);
    }

    private sealed class ProcessRule : CleanupRuleBase
    {
        private readonly List<string> _log;

        public ProcessRule(string id, List<string> log, string process)
            : base(id, CleanupCategory.Browsers, id, id, RiskLevel.Review)
        {
            _log = log;
            ProcessesToClose = [process];
            RequiresProcessesClosed = true;
        }

        public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
            Task.FromResult(CleanupAnalysis.Action(this, 1, "teste"));

        public override Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
            IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
        {
            _log.Add(Id);
            return Task.FromResult(new CleanupRunResult(this));
        }
    }

    private sealed class ThrowingRule() : CleanupRuleBase("t.throw", CleanupCategory.System, "Falha", "Falha", RiskLevel.Safe)
    {
        public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
