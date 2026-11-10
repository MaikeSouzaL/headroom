using System.Diagnostics;
using Headroom.Core.Optimization;
using Headroom.Tests.TestHelpers;
using Microsoft.Win32;

namespace Headroom.Tests.Optimization;

public sealed class PerformanceTweakTests : IDisposable
{
    private readonly string _keyPath = @"Software\HeadroomTests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly TempDirectory _temp = new();

    public PerformanceTweakTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_keyPath);
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        _temp.Dispose();
    }

    private PerformanceTweak CreateTweak() => new("teste", "Teste", "d", "Na hora", new[]
    {
        new TweakValue(@"A", "Numero", 0, 1),
        new TweakValue(@"A\B", "Texto", "100", "400"),
        new TweakValue(@"C", "NovoValor", 1, null),
    })
    {
        Root = _root,
    };

    [Fact]
    public void Apply_ThenRevert_RestoresExactPreviousValues()
    {
        using (var a = _root.CreateSubKey("A"))
        {
            a.SetValue("Numero", 7, RegistryValueKind.DWord);
        }

        using (var b = _root.CreateSubKey(@"A\B"))
        {
            b.SetValue("Texto", "250", RegistryValueKind.String);
        }

        var backup = new TweakBackupStore(_temp.Combine("backup.json"));
        var tweak = CreateTweak();
        Assert.False(tweak.IsApplied);

        tweak.Apply(backup);
        Assert.True(tweak.IsApplied);
        Assert.Equal(0, _root.OpenSubKey("A")!.GetValue("Numero"));
        Assert.Equal("100", _root.OpenSubKey(@"A\B")!.GetValue("Texto"));
        Assert.Equal(1, _root.OpenSubKey("C")!.GetValue("NovoValor"));

        // O backup sobrevive a reabrir o app.
        tweak.Revert(new TweakBackupStore(_temp.Combine("backup.json")));
        Assert.False(tweak.IsApplied);
        Assert.Equal(7, _root.OpenSubKey("A")!.GetValue("Numero"));
        Assert.Equal("250", _root.OpenSubKey(@"A\B")!.GetValue("Texto"));
        Assert.Null(_root.OpenSubKey("C")!.GetValue("NovoValor"));
    }

    [Fact]
    public void Revert_WithoutBackup_UsesWindowsDefaults()
    {
        var tweak = CreateTweak();
        var backupFile = _temp.Combine("backup.json");
        tweak.Apply(new TweakBackupStore(backupFile));
        File.Delete(backupFile);

        tweak.Revert(new TweakBackupStore(backupFile));

        Assert.Equal(1, _root.OpenSubKey("A")!.GetValue("Numero"));
        Assert.Equal("400", _root.OpenSubKey(@"A\B")!.GetValue("Texto"));
        Assert.Null(_root.OpenSubKey("C")!.GetValue("NovoValor"));
    }

    [Fact]
    public void ApplyTwice_KeepsTheOriginalBackup()
    {
        using (var a = _root.CreateSubKey("A"))
        {
            a.SetValue("Numero", 5, RegistryValueKind.DWord);
        }

        var backup = new TweakBackupStore(_temp.Combine("backup.json"));
        var tweak = CreateTweak();
        tweak.Apply(backup);
        tweak.Apply(backup);
        tweak.Revert(backup);

        Assert.Equal(5, _root.OpenSubKey("A")!.GetValue("Numero"));
    }

    [Fact]
    public void Catalog_OnlyTouchesCurrentUserAndHasUniqueIds()
    {
        Assert.Equal(PerformanceTweaks.All.Count, PerformanceTweaks.All.Select(t => t.Id).Distinct().Count());
        Assert.All(PerformanceTweaks.All, t =>
        {
            Assert.NotEmpty(t.Values);
            Assert.All(t.Values, v => Assert.False(Path.IsPathRooted(v.KeyPath)));
        });
    }
}

public class PerformanceAdvisorTests
{
    private static MemoryDetails Memory(double usedFraction, double commitFraction = 0.5)
    {
        const long total = 16L * 1024 * 1024 * 1024;
        return new MemoryDetails(total, (long)(total * (1 - usedFraction)), (long)(total * commitFraction), total,
            0, 0, 0, null, null, null, 200, 3000, 90000);
    }

    private static ProcessSample Process(string name, double cpu = 0, long memory = 100 * 1024 * 1024,
        bool hung = false, bool isProtected = false, params ProcessIssue[] issues) =>
        new(1, name, name, null, null, null, cpu, cpu, memory, memory, 0, hung, ProcessPriorityClass.Normal, isProtected, issues);

    [Fact]
    public void Advise_FlagsHungProgramsFullMemoryAndHeavyProcesses()
    {
        var snapshot = new PerformanceSnapshot(Memory(0.93), 0.4, new[]
        {
            Process("Travado", hung: true, issues: ProcessIssue.NotResponding),
            Process("Pesado", cpu: 0.35, issues: ProcessIssue.HighCpu),
            Process("svchost", cpu: 0.5, isProtected: true),
        }, TimeSpan.FromHours(3), StartupEnabledCount: 20, PowerPlanName: "Economia de energia", TweaksAvailable: 2);

        var advice = PerformanceAdvisor.Advise(snapshot);

        Assert.Equal(AdviceLevel.Critical, advice[0].Level);
        Assert.Contains(advice, a => a.Title.Contains("Travado") && a.ActionId == "tab.processes");
        Assert.Contains(advice, a => a.ActionId == "memory.free" && a.Level == AdviceLevel.Critical);
        Assert.Contains(advice, a => a.Title.Contains("Pesado"));
        Assert.Contains(advice, a => a.ActionId == "tab.startup" && a.Level == AdviceLevel.Warning);
        Assert.Contains(advice, a => a.ActionId == "tab.energy");
        Assert.Contains(advice, a => a.ActionId == "tab.tweaks");

        // Processos do sistema nunca são sugeridos para finalizar.
        Assert.DoesNotContain(advice, a => a.Detail.Contains("svchost"));
    }

    [Fact]
    public void Advise_SaysAllGoodWhenNothingIsHeavy()
    {
        var advice = PerformanceAdvisor.Advise(new PerformanceSnapshot(Memory(0.4), 0.05,
            new[] { Process("Leve") }, TimeSpan.FromHours(1), StartupEnabledCount: 3));

        Assert.Equal(AdviceLevel.Ok, advice[0].Level);
        Assert.DoesNotContain(advice, a => a.Level is AdviceLevel.Warning or AdviceLevel.Critical);
    }

    [Fact]
    public void Advise_RemindsToRestartAfterAWeek()
    {
        var advice = PerformanceAdvisor.Advise(new PerformanceSnapshot(Memory(0.4), 0.05,
            Array.Empty<ProcessSample>(), TimeSpan.FromDays(9)));

        Assert.Contains(advice, a => a.Title.Contains("9 dias"));
    }
}

public class ProcessMonitorTests
{
    [Fact]
    public void Sample_MeasuresProcessesAndProtectsSystemOnes()
    {
        var monitor = new ProcessMonitor();
        monitor.Sample();
        Thread.Sleep(300);
        var samples = monitor.Sample();

        var self = Assert.Single(samples, s => s.Id == Environment.ProcessId);
        Assert.True(self.WorkingSet > 0);
        Assert.NotNull(self.Path);
        Assert.All(samples, s => Assert.InRange(s.CpuFraction, 0, 1));
        Assert.All(samples.Where(s => s.Name.Equals("csrss", StringComparison.OrdinalIgnoreCase)), s => Assert.True(s.IsProtected));
        Assert.All(samples.Where(s => s.IsProtected), s => Assert.Empty(s.Issues));
    }

    [Fact]
    public void CpuSampler_ReturnsAFraction()
    {
        var sampler = new CpuSampler();
        Assert.Equal(0, sampler.Next());
        Thread.Sleep(200);
        Assert.InRange(sampler.Next(), 0, 1);
    }

    [Fact]
    public void MemoryDetails_AreConsistent()
    {
        var memory = MemoryOptimizer.GetDetails();
        Assert.True(memory.TotalBytes > 0);
        Assert.InRange(memory.AvailableBytes, 0, memory.TotalBytes);
        Assert.True(memory.CommitLimit >= memory.CommitTotal);
        Assert.True(memory.ProcessCount > 0);
    }

    [Fact]
    public void HasSeekPenalty_IsSafeForAnyPath()
    {
        Assert.Null(Core.Drives.DriveService.HasSeekPenalty(@"\\servidor\pasta"));
        Assert.Null(Core.Drives.DriveService.HasSeekPenalty(string.Empty));

        // O disco do sistema responde (SSD = false, HD = true) ou null sem permissão; nunca lança exceção.
        _ = Core.Drives.DriveService.HasSeekPenalty(Environment.SystemDirectory);
    }

    [Fact]
    public void KillAndPriority_RefuseProtectedProcesses()
    {
        using var csrss = Process.GetProcessesByName("csrss").FirstOrDefault();
        if (csrss is null)
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(() => SystemInfo.KillProcess(csrss.Id, "csrss"));
        Assert.Throws<InvalidOperationException>(() => SystemInfo.SetPriority(csrss.Id, "csrss", ProcessPriorityClass.Idle));
        Assert.Throws<ArgumentException>(() => SystemInfo.SetPriority(Environment.ProcessId, "x", ProcessPriorityClass.RealTime));
    }
}
