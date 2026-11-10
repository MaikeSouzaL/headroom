using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Services;
using Headroom.Core.Diagnostics;
using Headroom.Core.Formatting;
using Wpf.Ui.Controls;

namespace Headroom.App.ViewModels;

/// <summary>Um achado do diagnóstico na tela.</summary>
public sealed class FindingViewModel
{
    private static readonly Brush OkBrush = Frozen("#2F9E44");
    private static readonly Brush InfoBrush = Frozen("#1C7ED6");
    private static readonly Brush AttentionBrush = Frozen("#E67700");
    private static readonly Brush ProblemBrush = Frozen("#E03131");

    public FindingViewModel(DiagnosticFinding finding)
    {
        Finding = finding;
    }

    public DiagnosticFinding Finding { get; }
    public string Category => Finding.Category;
    public string Title => Finding.Title;
    public string Summary => Finding.Summary;
    public string? Recommendation => Finding.Recommendation;
    public string StatusText => HealthStatusInfo.DisplayName(Finding.Status);
    public string DetailsText => Finding.Details is { Count: > 0 } d ? string.Join("\n", d) : string.Empty;
    public bool HasAction => Finding.ActionId is not null;
    public string ActionText => Finding.ActionText ?? "Resolver";

    public Brush StatusBrush => Finding.Status switch
    {
        HealthStatus.Ok => OkBrush,
        HealthStatus.Info => InfoBrush,
        HealthStatus.Attention => AttentionBrush,
        _ => ProblemBrush,
    };

    public SymbolRegular Icon => Finding.Status switch
    {
        HealthStatus.Ok => SymbolRegular.CheckmarkCircle24,
        HealthStatus.Info => SymbolRegular.Info24,
        HealthStatus.Attention => SymbolRegular.Warning24,
        _ => SymbolRegular.ErrorCircle24,
    };

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Saúde do PC: diagnóstico somente leitura com nota, recomendações e ações sugeridas.</summary>
public sealed partial class HealthViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    public HealthViewModel(AppServices services)
    {
        _services = services;
    }

    public ObservableCollection<FindingViewModel> Findings { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isRunning;

    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private int _score;
    [ObservableProperty] private string _scoreText = string.Empty;
    [ObservableProperty] private string _summaryText = "Clique em \"Diagnosticar\" para verificar discos, memória, inicialização, drivers, segurança, energia e rede. Nada é alterado.";
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private Brush _scoreBrush = Brushes.Gray;
    [ObservableProperty] private bool _showOnlyIssues;

    private DiagnosticReport? _report;

    partial void OnShowOnlyIssuesChanged(bool value) => Populate();

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(p => ProgressText = p);
        try
        {
            _report = await new DiagnosticEngine().RunAsync(_services.IsAdministrator, progress, _cts.Token);
            Score = _report.Score;
            ScoreText = _report.ScoreText;
            ScoreBrush = new FindingViewModel(new DiagnosticFinding(string.Empty, string.Empty,
                _report.Score >= 90 ? HealthStatus.Ok : _report.Score >= 75 ? HealthStatus.Info : _report.Score >= 50 ? HealthStatus.Attention : HealthStatus.Problem,
                string.Empty)).StatusBrush;
            SummaryText = $"{_report.Problems} problema(s) e {_report.Attentions} ponto(s) de atenção em {_report.Findings.Count} verificações " +
                          $"({ByteSize.FormatDuration(_report.Elapsed)})." +
                          (_services.IsAdministrator ? string.Empty : " Rode como administrador para ver a saúde SMART dos discos e mais detalhes.");
            HasReport = true;
            Populate();
        }
        catch (OperationCanceledException)
        {
            SummaryText = "Diagnóstico cancelado.";
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Falha no diagnóstico", ex);
        }
        finally
        {
            IsRunning = false;
            ProgressText = string.Empty;
        }
    }

    private void Populate()
    {
        Findings.Clear();
        if (_report is null)
        {
            return;
        }

        foreach (var finding in _report.Findings.Where(f => !ShowOnlyIssues || f.Status is HealthStatus.Attention or HealthStatus.Problem))
        {
            Findings.Add(new FindingViewModel(finding));
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task RunActionAsync(FindingViewModel? finding)
    {
        string? action = finding?.Finding.ActionId;
        if (action is null)
        {
            return;
        }

        if (action.StartsWith("page:", StringComparison.Ordinal))
        {
            var page = action[5..] switch
            {
                "Cleanup" => PageKind.Cleanup,
                "Processes" or "Startup" => PageKind.Performance,
                _ => PageKind.Home,
            };
            _services.Navigator.Navigate(page);
            return;
        }

        _services.Navigator.Navigate(PageKind.Repairs);
        await Task.Yield();
        RepairRequested?.Invoke(this, action);
    }

    /// <summary>Pedido para a tela de Reparos destacar/executar um reparo.</summary>
    public static event EventHandler<string>? RepairRequested;
}
