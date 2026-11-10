using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Helpers;
using Headroom.App.Services;
using Headroom.Core.Formatting;
using Headroom.Core.Optimization;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Wpf.Ui.Controls;

namespace Headroom.App.ViewModels;

/// <summary>Item de inicialização com liga/desliga.</summary>
public sealed partial class StartupItemViewModel : ObservableObject
{
    private readonly PerformanceViewModel _owner;
    private bool _suppress;

    public StartupItemViewModel(StartupItem item, PerformanceViewModel owner)
    {
        Item = item;
        _owner = owner;
        _isEnabled = item.Enabled;
    }

    public StartupItem Item { get; }
    public string Name => Item.Name;
    public string Publisher => Item.Publisher ?? "Editor desconhecido";
    public string Command => Item.Command;
    public string Source => Item.SourceText;
    public bool IsBroken => Item.ExecutablePath is not null && Path.IsPathFullyQualified(Item.ExecutablePath) && !Item.ExecutableExists;

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_suppress)
        {
            _ = _owner.ToggleAsync(this, value);
        }
    }

    internal void Revert(bool value)
    {
        _suppress = true;
        IsEnabled = value;
        _suppress = false;
    }
}

/// <summary>Processo na lista (atualizado no lugar a cada medição, para a linha não "pular").</summary>
public sealed partial class ProcessRowViewModel : ObservableObject
{
    public ProcessRowViewModel(ProcessSample sample)
    {
        Id = sample.Id;
        Name = sample.Name;
        _sample = sample;
    }

    public int Id { get; }
    public string Name { get; }

    [ObservableProperty]
    private ProcessSample _sample;

    partial void OnSampleChanged(ProcessSample value) => OnPropertyChanged(string.Empty);

    public string DisplayName => Sample.DisplayName;
    public string Subtitle => Sample.WindowTitle is { Length: > 0 } title && title != Sample.DisplayName
        ? title
        : Sample.Company ?? Sample.Path ?? Sample.Name;
    public double Cpu => Sample.CpuAverage;
    public string CpuText => ByteSize.FormatPercent(Sample.CpuAverage);
    public long Memory => Sample.WorkingSet;
    public string MemoryText => ByteSize.Format(Sample.WorkingSet);
    public double Disk => Sample.DiskBytesPerSecond;
    public string DiskText => Sample.DiskBytesPerSecond < 1024 ? "0" : ByteSize.Format((long)Sample.DiskBytesPerSecond) + "/s";
    public bool IsHeavy => Sample.IsHeavy;
    public bool CanAct => !Sample.IsProtected;
    public bool IsLowered => Sample.Priority is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle;
    public string? Path => Sample.Path;

    public string PriorityText => Sample.Priority switch
    {
        ProcessPriorityClass.Idle => "Baixa",
        ProcessPriorityClass.BelowNormal => "Abaixo do normal",
        ProcessPriorityClass.Normal => "Normal",
        ProcessPriorityClass.AboveNormal => "Acima do normal",
        ProcessPriorityClass.High => "Alta",
        ProcessPriorityClass.RealTime => "Tempo real",
        _ => "—",
    };

    public string StatusText => Sample.IsProtected
        ? "Sistema"
        : string.Join(" · ", Sample.Issues.Select(i => i switch
        {
            ProcessIssue.HighCpu => "CPU alta",
            ProcessIssue.HighMemory => "Memória alta",
            ProcessIssue.HighDisk => "Disco alto",
            _ => "Não responde",
        }));
}

/// <summary>Recomendação exibida no Monitor.</summary>
public sealed class AdviceRowViewModel
{
    private static readonly Brush OkBrush = Frozen(Color.FromRgb(0x2F, 0x9E, 0x44));
    private static readonly Brush InfoBrush = Frozen(Color.FromRgb(0x0F, 0x6C, 0xBD));
    private static readonly Brush WarningBrush = Frozen(Color.FromRgb(0xE6, 0x77, 0x00));
    private static readonly Brush CriticalBrush = Frozen(Color.FromRgb(0xE0, 0x31, 0x31));

    public AdviceRowViewModel(PerformanceAdvice advice)
    {
        Advice = advice;
    }

    public PerformanceAdvice Advice { get; }
    public string Title => Advice.Title;
    public string Detail => Advice.Detail;
    public string? ActionText => Advice.ActionText;
    public bool HasAction => Advice.ActionId is not null;

    public SymbolRegular Icon => Advice.Level switch
    {
        AdviceLevel.Ok => SymbolRegular.CheckmarkCircle24,
        AdviceLevel.Info => SymbolRegular.Info24,
        AdviceLevel.Warning => SymbolRegular.Warning24,
        _ => SymbolRegular.ErrorCircle24,
    };

    public Brush IconBrush => Advice.Level switch
    {
        AdviceLevel.Ok => OkBrush,
        AdviceLevel.Info => InfoBrush,
        AdviceLevel.Warning => WarningBrush,
        _ => CriticalBrush,
    };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Ajuste de desempenho com liga/desliga (ligar = aplicar; desligar = desfazer).</summary>
public sealed partial class TweakRowViewModel : ObservableObject
{
    private readonly PerformanceViewModel _owner;
    private bool _suppress;

    public TweakRowViewModel(PerformanceTweak tweak, PerformanceViewModel owner)
    {
        Tweak = tweak;
        _owner = owner;
        _isApplied = SafeIsApplied(tweak);
    }

    public PerformanceTweak Tweak { get; }
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string WhenItApplies => $"Vale: {Tweak.WhenItApplies}";

    [ObservableProperty]
    private bool _isApplied;

    partial void OnIsAppliedChanged(bool value)
    {
        if (!_suppress)
        {
            _ = _owner.ToggleTweakAsync(this, value);
        }
    }

    internal void Set(bool value)
    {
        _suppress = true;
        IsApplied = value;
        _suppress = false;
    }

    private static bool SafeIsApplied(PerformanceTweak tweak)
    {
        try
        {
            return tweak.IsApplied;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// Desempenho: monitor ao vivo de CPU e memória, liberar memória, processos pesados/travados (finalizar ou
/// baixar prioridade), inicialização, ajustes reversíveis e plano de energia.
/// </summary>
public sealed partial class PerformanceViewModel : ObservableObject, IPageActivated, IPageDeactivated
{
    public const int TabMonitor = 0;
    public const int TabProcesses = 1;
    public const int TabStartup = 2;
    public const int TabTweaks = 3;
    public const int TabEnergy = 4;

    private const int HistoryLength = 60;

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly CpuSampler _cpuSampler = new();
    private readonly ProcessMonitor _processMonitor = new();
    private readonly Dictionary<int, ProcessRowViewModel> _rows = new();
    private readonly Queue<double> _recentCpu = new();
    private readonly TweakBackupStore _tweakBackup = new();
    private IReadOnlyList<ProcessSample> _lastSamples = Array.Empty<ProcessSample>();
    private MemoryDetails? _lastMemory;
    private bool _loaded;
    private bool _sampling;
    private bool _loadingPlans;
    private int _tick;
    private int _samplesTaken;

    public PerformanceViewModel(AppServices services)
    {
        _services = services;
        _freeStandby = services.IsAdministrator;

        var (cpuName, mhz) = SystemInfo.GetProcessorInfo();
        CpuName = cpuName;
        _cpuClock = mhz > 0 ? $"{mhz / 1000.0:0.0#} GHz" : string.Empty;

        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        ProcessesView.Filter = FilterProcess;
        ProcessesView.SortDescriptions.Add(new SortDescription(nameof(ProcessRowViewModel.IsHeavy), ListSortDirection.Descending));
        ProcessesView.SortDescriptions.Add(new SortDescription(nameof(ProcessRowViewModel.Cpu), ListSortDirection.Descending));
        ProcessesView.SortDescriptions.Add(new SortDescription(nameof(ProcessRowViewModel.Memory), ListSortDirection.Descending));

        foreach (var tweak in PerformanceTweaks.All)
        {
            Tweaks.Add(new TweakRowViewModel(tweak, this));
        }

        BuildChart();
        services.Theme.ThemeChanged += (_, _) => BuildChart();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    private readonly string _cpuClock;

    public bool IsAdministrator => _services.IsAdministrator;

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isLoading;

    // ---------- Monitor ----------

    public string CpuName { get; }
    public ObservableCollection<double> CpuHistory { get; } = new();
    public ObservableCollection<double> MemoryHistory { get; } = new();
    public ObservableCollection<AdviceRowViewModel> Advice { get; } = new();

    [ObservableProperty] private ISeries[] _monitorSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _monitorXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _monitorYAxes = Array.Empty<Axis>();
    [ObservableProperty] private SolidColorPaint? _legendPaint;

    [ObservableProperty] private double _cpuFraction;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _cpuDetail = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMemoryCritical))]
    private double _memoryFraction;

    public bool IsMemoryCritical => MemoryFraction >= 0.90;
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private string _memoryDetail = string.Empty;
    [ObservableProperty] private string _processCountText = "—";
    [ObservableProperty] private string _processDetail = string.Empty;

    [ObservableProperty] private bool _freeWorkingSets = true;
    [ObservableProperty] private bool _freeStandby;
    [ObservableProperty] private bool _freeFileCache;
    [ObservableProperty] private bool _isFreeingMemory;
    [ObservableProperty] private string? _freeMemoryResult;

    // ---------- Processos ----------

    public ObservableCollection<ProcessRowViewModel> Processes { get; } = new();
    public ICollectionView ProcessesView { get; }

    [ObservableProperty] private bool _onlyHeavy;
    [ObservableProperty] private string _processSearch = string.Empty;
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private string _processSummary = "Medindo…";

    partial void OnOnlyHeavyChanged(bool value) => ProcessesView.Refresh();
    partial void OnProcessSearchChanged(string value) => ProcessesView.Refresh();

    // ---------- Inicialização, ajustes e energia ----------

    public ObservableCollection<StartupItemViewModel> StartupItems { get; } = new();
    public ObservableCollection<TweakRowViewModel> Tweaks { get; } = new();
    public ObservableCollection<PowerPlan> PowerPlans { get; } = new();

    [ObservableProperty] private string _startupSummary = string.Empty;
    [ObservableProperty] private string _tweaksSummary = string.Empty;
    [ObservableProperty] private PowerPlan? _selectedPowerPlan;

    public async void OnActivated()
    {
        _timer.Start();
        if (!_loaded)
        {
            _loaded = true;
            UpdateTweaksSummary();
            await TickAsync();
            await RefreshAsync();
        }
    }

    public void OnDeactivated() => _timer.Stop();

    /// <summary>A cada segundo: CPU e memória; a cada 3 segundos: processos e recomendações.</summary>
    private async Task TickAsync()
    {
        double cpu = _cpuSampler.Next();
        var memory = MemoryOptimizer.GetDetails();
        _lastMemory = memory;

        _recentCpu.Enqueue(cpu);
        while (_recentCpu.Count > 5)
        {
            _recentCpu.Dequeue();
        }

        Push(CpuHistory, cpu * 100);
        Push(MemoryHistory, memory.UsedFraction * 100);

        CpuFraction = cpu;
        CpuText = ByteSize.FormatPercent(cpu);
        CpuDetail = $"{Environment.ProcessorCount} núcleos lógicos" +
                    (_cpuClock.Length > 0 ? $" · {_cpuClock}" : string.Empty) +
                    $" · ligado há {FormatUptime(SystemInfo.Uptime)}";
        MemoryFraction = memory.UsedFraction;
        MemoryText = $"{ByteSize.Format(memory.UsedBytes)} de {ByteSize.Format(memory.TotalBytes)}";
        MemoryDetail = $"{ByteSize.FormatPercent(memory.UsedFraction)} em uso · disponível {ByteSize.Format(memory.AvailableBytes)}" +
                       (memory.StandbyBytes is long standby ? $" · em cache (standby) {ByteSize.Format(standby)}" : string.Empty) +
                       $" · comprometida {ByteSize.Format(memory.CommitTotal)} de {ByteSize.Format(memory.CommitLimit)}";
        ProcessCountText = ByteSize.FormatCount(memory.ProcessCount);
        ProcessDetail = $"{ByteSize.FormatCount(memory.ThreadCount)} threads · {ByteSize.FormatCount(memory.HandleCount)} handles";

        if (_tick++ % 3 == 0 && !_sampling && (AutoRefresh || SelectedTab == TabMonitor || _lastSamples.Count == 0))
        {
            await SampleProcessesAsync(resort: false);
        }
    }

    private async Task SampleProcessesAsync(bool resort)
    {
        if (_sampling)
        {
            return;
        }

        _sampling = true;
        try
        {
            _lastSamples = await Task.Run(_processMonitor.Sample);

            // As primeiras medições ainda não têm média de CPU; reordena até ela estabilizar.
            ApplySamples(_lastSamples, resort || ++_samplesTaken <= 4);
            UpdateAdvice();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            _sampling = false;
        }
    }

    private void ApplySamples(IReadOnlyList<ProcessSample> samples, bool resort)
    {
        var alive = new HashSet<int>();
        foreach (var sample in samples)
        {
            alive.Add(sample.Id);
            if (_rows.TryGetValue(sample.Id, out var row) && row.Name == sample.Name)
            {
                row.Sample = sample;
            }
            else
            {
                if (row is not null)
                {
                    Processes.Remove(row);
                }

                row = new ProcessRowViewModel(sample);
                _rows[sample.Id] = row;
                Processes.Add(row);
            }
        }

        foreach (var gone in _rows.Where(r => !alive.Contains(r.Key)).ToList())
        {
            Processes.Remove(gone.Value);
            _rows.Remove(gone.Key);
        }

        // Reordenar mexe nas linhas; só quando pedido ou quando o filtro "só os pesados" está ligado.
        if (resort || OnlyHeavy)
        {
            ProcessesView.Refresh();
        }

        int heavy = samples.Count(s => s.IsHeavy);
        int hung = samples.Count(s => s.NotResponding && !s.IsProtected);
        ProcessSummary = $"{ByteSize.FormatCount(samples.Count)} processos · " +
                         (heavy == 0 ? "nenhum pesando agora" : $"{heavy} pesando agora") +
                         (hung > 0 ? $" · {hung} não responde(m)" : string.Empty) +
                         ". CPU é a média dos últimos segundos.";
    }

    private void UpdateAdvice()
    {
        if (_lastMemory is null)
        {
            return;
        }

        var snapshot = new PerformanceSnapshot(_lastMemory, _recentCpu.Count == 0 ? 0 : _recentCpu.Average(), _lastSamples,
            SystemInfo.Uptime,
            StartupItems.Count == 0 ? null : StartupItems.Count(i => i.IsEnabled),
            PowerPlans.FirstOrDefault(p => p.IsActive)?.Name,
            Tweaks.Count(t => !t.IsApplied));

        var advice = PerformanceAdvisor.Advise(snapshot);
        bool same = advice.Count == Advice.Count && advice.Zip(Advice, (a, b) => a == b.Advice).All(x => x);
        if (!same)
        {
            Advice.Clear();
            foreach (var item in advice)
            {
                Advice.Add(new AdviceRowViewModel(item));
            }
        }
    }

    private bool FilterProcess(object item)
    {
        if (item is not ProcessRowViewModel row)
        {
            return false;
        }

        if (OnlyHeavy && !row.IsHeavy)
        {
            return false;
        }

        string search = ProcessSearch.Trim();
        return search.Length == 0 ||
               row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static void Push(ObservableCollection<double> history, double value)
    {
        history.Add(Math.Round(value, 1));
        while (history.Count > HistoryLength)
        {
            history.RemoveAt(0);
        }
    }

    private static string FormatUptime(TimeSpan uptime) => uptime.TotalDays >= 1
        ? $"{(int)uptime.TotalDays} d {uptime.Hours} h"
        : $"{uptime.Hours} h {uptime.Minutes} min";

    private void BuildChart()
    {
        bool dark = _services.Theme.IsDark;
        var cpuColor = dark ? ChartPalette.AccentLight : ChartPalette.Accent;
        var ramColor = SKColor.Parse("#8764B8");

        MonitorSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Name = "Processador",
                Values = CpuHistory,
                GeometrySize = 0,
                LineSmoothness = 0.3,
                Stroke = ChartPalette.Stroke(cpuColor, 2),
                Fill = ChartPalette.Paint(cpuColor.WithAlpha(40)),
                YToolTipLabelFormatter = point => $"{point.Coordinate.PrimaryValue:0}%",
            },
            new LineSeries<double>
            {
                Name = "Memória",
                Values = MemoryHistory,
                GeometrySize = 0,
                LineSmoothness = 0.3,
                Stroke = ChartPalette.Stroke(ramColor, 2),
                Fill = null,
                YToolTipLabelFormatter = point => $"{point.Coordinate.PrimaryValue:0}%",
            },
        };

        MonitorXAxes = new[]
        {
            new Axis { MinLimit = 0, MaxLimit = HistoryLength - 1, IsVisible = false },
        };

        MonitorYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                MaxLimit = 100,
                MinStep = 25,
                Labeler = value => $"{value:0}%",
                LabelsPaint = ChartPalette.Paint(ChartPalette.MutedText(dark)),
                SeparatorsPaint = ChartPalette.Stroke(ChartPalette.Grid(dark)),
                TextSize = 11,
            },
        };

        LegendPaint = ChartPalette.Paint(ChartPalette.Text(dark));
    }

    // ---------- Liberar memória ----------

    [RelayCommand]
    private async Task FreeMemoryAsync()
    {
        var areas = MemoryCleanupAreas.None;
        if (FreeWorkingSets)
        {
            areas |= MemoryCleanupAreas.WorkingSets;
        }

        if (FreeStandby)
        {
            areas |= MemoryCleanupAreas.ModifiedList | MemoryCleanupAreas.StandbyList;
        }

        if (FreeFileCache)
        {
            areas |= MemoryCleanupAreas.SystemFileCache;
        }

        if (areas == MemoryCleanupAreas.None)
        {
            await _services.Dialogs.InfoAsync("Liberar memória", "Marque pelo menos uma opção.");
            return;
        }

        IsFreeingMemory = true;
        FreeMemoryResult = null;
        try
        {
            var result = await MemoryOptimizer.FreeAsync(areas);
            FreeMemoryResult = $"Disponível antes: {ByteSize.Format(result.AvailableBefore)} · agora: {ByteSize.Format(result.AvailableAfter)} " +
                               $"(+{ByteSize.Format(result.FreedBytes)}). " +
                               string.Join(". ", result.Done) +
                               (result.Skipped.Count > 0 ? ". Pulado: " + string.Join(", ", result.Skipped) : string.Empty) + ".";
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Não foi possível liberar memória", ex);
        }
        finally
        {
            IsFreeingMemory = false;
        }
    }

    [RelayCommand]
    private async Task RunAdviceActionAsync(AdviceRowViewModel? row)
    {
        switch (row?.Advice.ActionId)
        {
            case "memory.free":
                await FreeMemoryAsync();
                break;
            case "tab.processes":
                OnlyHeavy = _lastSamples.Any(s => s.IsHeavy);
                SelectedTab = TabProcesses;
                await SampleProcessesAsync(resort: true);
                break;
            case "tab.startup":
                SelectedTab = TabStartup;
                break;
            case "tab.tweaks":
                SelectedTab = TabTweaks;
                break;
            case "tab.energy":
                SelectedTab = TabEnergy;
                break;
            case "page:Cleanup":
                _services.Navigator.Navigate(PageKind.Cleanup);
                break;
            case "page:Repairs":
                _services.Navigator.Navigate(PageKind.Repairs);
                break;
        }
    }

    // ---------- Processos ----------

    [RelayCommand]
    private Task RefreshProcessesAsync() => SampleProcessesAsync(resort: true);

    [RelayCommand]
    private Task ShowProcessesAsync()
    {
        SelectedTab = TabProcesses;
        return SampleProcessesAsync(resort: true);
    }

    [RelayCommand]
    private async Task KillProcessAsync(ProcessRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.CanAct)
        {
            await _services.Dialogs.InfoAsync("Processo protegido",
                "Este processo é essencial para o Windows (ou é um serviço) e não pode ser finalizado aqui.");
            return;
        }

        if (!await _services.Dialogs.ConfirmAsync("Finalizar processo",
                $"Finalizar \"{row.DisplayName}\" ({row.Name}, {row.MemoryText})?\n\n" +
                "Trabalho não salvo nesse programa será perdido. Processos filhos dele também serão finalizados.",
                "Finalizar", danger: true))
        {
            return;
        }

        try
        {
            SystemInfo.KillProcess(row.Id, row.Name);
            Processes.Remove(row);
            _rows.Remove(row.Id);
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Não foi possível finalizar", ex);
        }
    }

    [RelayCommand]
    private Task LowerPriorityAsync(ProcessRowViewModel? row) =>
        ChangePriorityAsync(row, row?.IsLowered == true ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal);

    private async Task ChangePriorityAsync(ProcessRowViewModel? row, ProcessPriorityClass priority)
    {
        if (row is null || !row.CanAct)
        {
            return;
        }

        try
        {
            SystemInfo.SetPriority(row.Id, row.Name, priority);
            row.Sample = row.Sample with { Priority = priority };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            await _services.Dialogs.InfoAsync("Sem permissão",
                $"O Windows não deixou mudar a prioridade de \"{row.DisplayName}\". Ele provavelmente roda como administrador; " +
                "reinicie o Headroom como administrador e tente de novo.");
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Não foi possível mudar a prioridade", ex);
        }
    }

    [RelayCommand]
    private void OpenProcessLocation(ProcessRowViewModel? row)
    {
        if (row?.Path is { } path && File.Exists(path))
        {
            _services.Shell.OpenInExplorer(path);
        }
    }

    // ---------- Inicialização e energia ----------

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var startup = await StartupManager.ListAsync();
            StartupItems.Clear();
            foreach (var item in startup.OrderByDescending(i => i.Enabled).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                StartupItems.Add(new StartupItemViewModel(item, this));
            }

            UpdateStartupSummary();

            _loadingPlans = true;
            PowerPlans.Clear();
            foreach (var plan in await SystemInfo.GetPowerPlansAsync())
            {
                PowerPlans.Add(plan);
            }

            SelectedPowerPlan = PowerPlans.FirstOrDefault(p => p.IsActive);
            _loadingPlans = false;

            foreach (var tweak in Tweaks)
            {
                tweak.Set(tweak.Tweak.IsApplied);
            }

            UpdateTweaksSummary();
            await SampleProcessesAsync(resort: true);
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Não foi possível carregar", ex);
        }
        finally
        {
            IsLoading = false;
            _loadingPlans = false;
        }
    }

    private void UpdateStartupSummary()
    {
        int enabled = StartupItems.Count(i => i.IsEnabled);
        int broken = StartupItems.Count(i => i.IsBroken);
        StartupSummary = $"{enabled} de {StartupItems.Count} itens iniciam com o Windows." +
                         (broken > 0 ? $" {broken} apontam para programas que não existem mais." : string.Empty) +
                         " Desativar é reversível (igual ao Gerenciador de Tarefas).";
    }

    internal async Task ToggleAsync(StartupItemViewModel item, bool enabled)
    {
        if (item.Item.RequiresAdmin && !_services.IsAdministrator)
        {
            item.Revert(!enabled);
            await _services.Dialogs.InfoAsync("Requer administrador",
                "Este item vale para todos os usuários do PC; alterá-lo exige executar o Headroom como administrador.");
            return;
        }

        try
        {
            await StartupManager.SetEnabledAsync(item.Item, enabled);
            UpdateStartupSummary();
        }
        catch (Exception ex)
        {
            item.Revert(!enabled);
            await _services.Dialogs.ErrorAsync("Não foi possível alterar", ex);
        }
    }

    [RelayCommand]
    private void OpenItemLocation(StartupItemViewModel? item)
    {
        if (item?.Item.ExecutablePath is { } path && (File.Exists(path) || Directory.Exists(path)))
        {
            _services.Shell.OpenInExplorer(path);
        }
    }

    async partial void OnSelectedPowerPlanChanged(PowerPlan? value)
    {
        if (_loadingPlans || value is null || value.IsActive)
        {
            return;
        }

        if (await SystemInfo.SetActivePowerPlanAsync(value.Id))
        {
            _loadingPlans = true;
            var plans = await SystemInfo.GetPowerPlansAsync();
            PowerPlans.Clear();
            foreach (var plan in plans)
            {
                PowerPlans.Add(plan);
            }

            SelectedPowerPlan = PowerPlans.FirstOrDefault(p => p.IsActive);
            _loadingPlans = false;
        }
        else
        {
            await _services.Dialogs.InfoAsync("Plano de energia", "Não foi possível ativar o plano escolhido.");
        }
    }

    // ---------- Ajustes ----------

    internal async Task ToggleTweakAsync(TweakRowViewModel row, bool apply)
    {
        try
        {
            if (apply)
            {
                row.Tweak.Apply(_tweakBackup);
            }
            else
            {
                row.Tweak.Revert(_tweakBackup);
            }

            row.Set(row.Tweak.IsApplied);
            UpdateTweaksSummary();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            row.Set(!apply);
            await _services.Dialogs.InfoAsync("Requer administrador",
                "O Windows bloqueou esta alteração para o seu usuário. Reinicie o Headroom como administrador e tente de novo.");
        }
        catch (Exception ex)
        {
            row.Set(!apply);
            await _services.Dialogs.ErrorAsync("Não foi possível alterar o ajuste", ex);
        }
    }

    [RelayCommand]
    private async Task ApplyAllTweaksAsync()
    {
        var pending = Tweaks.Where(t => !t.IsApplied).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        if (!await _services.Dialogs.ConfirmAsync("Aplicar ajustes",
                $"Aplicar {pending.Count} ajuste(s)?\n\n" + string.Join("\n", pending.Select(t => "• " + t.Name)) +
                "\n\nTodos podem ser desfeitos aqui mesmo, voltando aos valores de antes.", "Aplicar"))
        {
            return;
        }

        foreach (var row in pending)
        {
            await ToggleTweakAsync(row, apply: true);
        }
    }

    [RelayCommand]
    private async Task RevertAllTweaksAsync()
    {
        foreach (var row in Tweaks.Where(t => t.IsApplied).ToList())
        {
            await ToggleTweakAsync(row, apply: false);
        }
    }

    private void UpdateTweaksSummary()
    {
        int applied = Tweaks.Count(t => t.IsApplied);
        TweaksSummary = $"{applied} de {Tweaks.Count} ajustes aplicados. Só mudam configurações do seu usuário; " +
                        "nada de segurança, atualizações ou serviços do Windows é desligado. Desligar a chave desfaz o ajuste.";
    }
}
