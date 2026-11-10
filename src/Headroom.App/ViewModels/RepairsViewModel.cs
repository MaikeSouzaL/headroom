using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Services;
using Headroom.Core.Cleanup;
using Headroom.Core.Repairs;

namespace Headroom.App.ViewModels;

/// <summary>Um reparo na lista.</summary>
public sealed partial class RepairItemViewModel : ObservableObject
{
    public RepairItemViewModel(RepairAction action, bool isAdministrator)
    {
        Action = action;
        IsBlocked = action.RequiresAdmin && !isAdministrator;
    }

    public RepairAction Action { get; }
    public string Category => Action.Category;
    public string Name => Action.Name;
    public string Description => Action.Description;
    public string RiskText => CleanupCategoryInfo.RiskName(Action.Risk);
    public bool IsBlocked { get; }
    public bool RequiresAdmin => Action.RequiresAdmin;
    public bool RequiresReboot => Action.RequiresReboot;
    public string? Duration => Action.Duration is null ? null : "Duração: " + Action.Duration;

    public System.Windows.Media.Brush RiskBrush => Action.Risk switch
    {
        RiskLevel.Safe => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["RiskSafeBrush"],
        RiskLevel.Review => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["RiskReviewBrush"],
        _ => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["RiskAdvancedBrush"],
    };

    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private string _lastResult = string.Empty;
}

/// <summary>Reparos e utilitários de manutenção do Windows ("Corrigir erros").</summary>
public sealed partial class RepairsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly StringBuilder _output = new();
    private CancellationTokenSource? _cts;

    public RepairsViewModel(AppServices services)
    {
        _services = services;
        foreach (var action in RepairCatalog.All.Where(a => a.IsAvailable?.Invoke() ?? true))
        {
            Repairs.Add(new RepairItemViewModel(action, services.IsAdministrator));
        }

        var view = CollectionViewSource.GetDefaultView(Repairs);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RepairItemViewModel.Category)));
        RepairsView = view;
        HealthViewModel.RepairRequested += (_, id) => Highlight(id);
    }

    public ObservableCollection<RepairItemViewModel> Repairs { get; } = new();
    public System.ComponentModel.ICollectionView RepairsView { get; }
    public bool IsAdministrator => _services.IsAdministrator;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _outputText = "A saída dos comandos aparece aqui.";
    [ObservableProperty] private string _currentName = string.Empty;
    [ObservableProperty] private RepairItemViewModel? _selected;

    private void Highlight(string id)
    {
        foreach (var repair in Repairs)
        {
            repair.IsHighlighted = repair.Action.Id == id;
        }

        Selected = Repairs.FirstOrDefault(r => r.Action.Id == id);
    }

    private bool CanRun(RepairItemViewModel? item) => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync(RepairItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsBlocked)
        {
            await _services.Dialogs.InfoAsync("Requer administrador",
                "Este reparo precisa de privilégios de administrador. Use \"Reiniciar como administrador\" no menu lateral.");
            return;
        }

        string message = item.Description +
                         (item.Action.CreateRestorePoint ? "\n\nUm ponto de restauração será criado antes." : string.Empty) +
                         (item.RequiresReboot ? "\n\nSerá necessário reiniciar o PC depois." : string.Empty) +
                         (item.Duration is null ? string.Empty : "\n\n" + item.Duration + ".");
        if (!await _services.Dialogs.ConfirmAsync(item.Name, message, "Executar", danger: item.Action.Risk == RiskLevel.Advanced))
        {
            return;
        }

        IsRunning = true;
        CurrentName = item.Name;
        _output.Clear();
        _output.AppendLine($"=== {item.Name} — {DateTime.Now:dd/MM/yyyy HH:mm:ss} ===");
        OutputText = _output.ToString();
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(line =>
        {
            _output.AppendLine(line);
            if (_output.Length > 200_000)
            {
                _output.Remove(0, _output.Length - 150_000);
            }

            OutputText = _output.ToString();
        });

        try
        {
            var result = await RepairCatalog.RunAsync(item.Action, _services.IsAdministrator, progress, _cts.Token);
            _output.AppendLine();
            _output.AppendLine(result.Message);
            if (result.LogPath is not null)
            {
                _output.AppendLine("Log: " + result.LogPath);
            }

            item.LastResult = $"{DateTime.Now:HH:mm} — {result.Message}";
        }
        catch (OperationCanceledException)
        {
            _output.AppendLine("Cancelado.");
            item.LastResult = "Cancelado.";
        }
        catch (Exception ex)
        {
            _output.AppendLine("Erro: " + ex.Message);
            item.LastResult = "Erro: " + ex.Message;
        }
        finally
        {
            OutputText = _output.ToString();
            IsRunning = false;
            CurrentName = string.Empty;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void CopyOutput() => _services.Shell.CopyPath(OutputText);
}
