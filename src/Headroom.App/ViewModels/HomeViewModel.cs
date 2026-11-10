using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Services;
using Headroom.Core.Drives;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;
using Wpf.Ui.Controls;

namespace Headroom.App.ViewModels;

/// <summary>Cartão de um disco na tela inicial.</summary>
public sealed partial class DriveCardViewModel : ObservableObject
{
    public DriveCardViewModel(DriveSummary drive)
    {
        Drive = drive;
    }

    public DriveSummary Drive { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>"Última análise: 15/10/2026 14:20 · 109 GB" quando há análise salva deste disco.</summary>
    [ObservableProperty]
    private string? _savedText;

    public string Letter => Drive.Letter;
    public string Title => $"{Drive.DisplayName} ({Drive.Letter})";
    public string MediaKind => Drive.MediaKindText;
    public string FileSystem => Drive.FileSystem;
    public string? Model => Drive.Model;
    public double UsedFraction => Drive.UsedFraction;
    public bool IsCritical => Drive.IsCritical;
    public string TotalText => ByteSize.Format(Drive.TotalBytes);
    public string UsedText => $"Usado: {ByteSize.Format(Drive.UsedBytes)} ({ByteSize.FormatPercent(Drive.UsedFraction)})";
    public string FreeText => $"{ByteSize.Format(Drive.FreeBytes)} livres de {ByteSize.Format(Drive.TotalBytes)}";
    public bool IsSystemDrive => Drive.IsSystemDrive;

    public SymbolRegular Icon => Drive.MediaKind switch
    {
        DriveMediaKind.Usb or DriveMediaKind.Removable => SymbolRegular.UsbStick24,
        DriveMediaKind.Network => SymbolRegular.Globe24,
        DriveMediaKind.Optical => SymbolRegular.Cd16,
        _ => SymbolRegular.HardDrive20,
    };
}

/// <summary>Tela inicial: escolha dos discos (um ou vários) ou de uma pasta a escanear.</summary>
public sealed partial class HomeViewModel : ObservableObject, IPageActivated
{
    private readonly AppServices _services;
    private readonly Dictionary<string, SavedScanInfo> _saved = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingSelection;

    public HomeViewModel(AppServices services)
    {
        _services = services;
        IsAdministrator = services.IsAdministrator;
        RefreshDrives();
    }

    /// <summary>Ao voltar para o Início, relê as análises salvas (podem ter sido criadas ou atualizadas).</summary>
    public async void OnActivated()
    {
        var infos = await Task.Run(() => ScanArchive.List());
        _saved.Clear();
        foreach (var info in infos)
        {
            _saved[Normalize(info.RootPath)] = info;
        }

        foreach (var card in Drives)
        {
            card.SavedText = _saved.TryGetValue(Normalize(card.Drive.RootPath), out var info)
                ? $"Última análise: {info.ScannedAt:dd/MM/yyyy HH:mm} · {ByteSize.Format(info.TotalBytes)}"
                : null;
        }

        UpdateTargets();
    }

    private static string Normalize(string path) => path.TrimEnd('\\').ToUpperInvariant();

    /// <summary>Todos os discos/pastas escolhidos já têm análise salva (dá para abrir sem escanear).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSavedCommand))]
    private bool _hasSavedForAll;

    [ObservableProperty]
    private string _openSavedButtonText = "Abrir análise salva";

    private bool CanOpenSaved() => HasSavedForAll;

    [RelayCommand(CanExecute = nameof(CanOpenSaved))]
    private Task OpenSavedAsync() => _services.Navigator.OpenSavedAsync(Targets);

    public ObservableCollection<DriveCardViewModel> Drives { get; } = new();

    public bool IsAdministrator { get; }

    /// <summary>Pasta escolhida em "Escolher uma pasta…" (exclusiva: desmarca os discos).</summary>
    [ObservableProperty]
    private string? _folderPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private IReadOnlyList<string> _targets = Array.Empty<string>();

    [ObservableProperty]
    private string _targetText = "Nenhum disco ou pasta selecionado.";

    [ObservableProperty]
    private string _scanButtonText = "Escanear";

    [ObservableProperty]
    private string? _lowSpaceWarning;

    [RelayCommand]
    private void RefreshDrives()
    {
        var previous = Drives.Where(d => d.IsSelected).Select(d => d.Drive.RootPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool firstLoad = Drives.Count == 0 && FolderPath is null;

        _updatingSelection = true;
        Drives.Clear();
        foreach (var drive in DriveService.GetDrives())
        {
            var card = new DriveCardViewModel(drive) { IsSelected = previous.Contains(drive.RootPath) };
            card.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DriveCardViewModel.IsSelected))
                {
                    OnDriveSelectionChanged(card);
                }
            };
            Drives.Add(card);
        }

        // Primeira abertura: vêm marcados os discos da última análise; sem análise anterior, o disco do sistema.
        if (firstLoad)
        {
            var last = Drives.Where(d => _services.Settings.LastAnalysisRoots.Contains(d.Drive.RootPath, StringComparer.OrdinalIgnoreCase)).ToList();
            if (last.Count > 0)
            {
                last.ForEach(d => d.IsSelected = true);
            }
            else if ((Drives.FirstOrDefault(d => d.IsSystemDrive) ?? Drives.FirstOrDefault()) is { } suggested)
            {
                suggested.IsSelected = true;
            }
        }

        _updatingSelection = false;
        UpdateTargets();
        UpdateLowSpaceWarning();
    }

    private void OnDriveSelectionChanged(DriveCardViewModel card)
    {
        if (_updatingSelection)
        {
            return;
        }

        // Marcar um disco descarta a pasta escolhida (são alternativas).
        if (card.IsSelected)
        {
            FolderPath = null;
        }

        UpdateTargets();
    }

    [RelayCommand]
    private void SelectAllDrives() => SetAllDrives(true);

    [RelayCommand]
    private void ClearDriveSelection() => SetAllDrives(false);

    private void SetAllDrives(bool value)
    {
        _updatingSelection = true;
        foreach (var drive in Drives)
        {
            drive.IsSelected = value;
        }

        _updatingSelection = false;
        if (value)
        {
            FolderPath = null;
        }

        UpdateTargets();
    }

    [RelayCommand]
    private void ChooseFolder()
    {
        string? folder = _services.Dialogs.PickFolder("Escolha a pasta a escanear", FolderPath);
        if (folder is null)
        {
            return;
        }

        _updatingSelection = true;
        foreach (var drive in Drives)
        {
            drive.IsSelected = false;
        }

        _updatingSelection = false;
        FolderPath = folder;
        UpdateTargets();
    }

    private void UpdateTargets()
    {
        UpdateTargetsCore();

        var saved = Targets.Select(t => _saved.TryGetValue(Normalize(t), out var info) ? info : null).ToList();
        HasSavedForAll = saved.Count > 0 && saved.All(s => s is not null);
        OpenSavedButtonText = saved.Count > 1 ? $"Abrir {saved.Count} análises salvas" : "Abrir análise salva";
        if (HasSavedForAll)
        {
            ScanButtonText = "Nova análise";
            var oldest = saved.Min(s => s!.ScannedAt);
            TargetText = TargetText.TrimEnd('.') + "." + (saved.Count == 1
                ? $" Há uma análise salva de {oldest:dd/MM/yyyy HH:mm}: abra sem escanear ou faça uma nova."
                : $" Todos têm análise salva (a mais antiga de {oldest:dd/MM/yyyy HH:mm}).");
        }
    }

    private void UpdateTargetsCore()
    {
        if (FolderPath is not null)
        {
            Targets = new[] { FolderPath };
            TargetText = $"Pasta selecionada: {FolderPath}";
            ScanButtonText = "Escanear pasta";
            return;
        }

        var selected = Drives.Where(d => d.IsSelected).ToList();
        Targets = selected.Select(d => d.Drive.RootPath).ToList();
        long used = selected.Sum(d => d.Drive.UsedBytes);
        TargetText = selected.Count switch
        {
            0 => "Nenhum disco ou pasta selecionado. Marque um ou mais discos.",
            1 => $"Selecionado: {selected[0].Letter} ({ByteSize.Format(used)} usados)",
            _ => $"Selecionados: {string.Join(", ", selected.Select(d => d.Letter))} ({ByteSize.Format(used)} usados no total). " +
                 "Os discos são escaneados um após o outro.",
        };
        ScanButtonText = selected.Count > 1 ? $"Escanear {selected.Count} discos" : "Escanear";
    }

    private bool CanScan() => Targets.Count > 0;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync() => _services.Navigator.StartScanAsync(Targets);

    [RelayCommand]
    private async Task RestartAsAdminAsync()
    {
        if (!_services.Shell.RestartAsAdministrator())
        {
            await _services.Dialogs.InfoAsync("Elevação cancelada",
                "O Headroom continua sem privilégios de administrador. Algumas pastas do sistema serão puladas no scan.");
        }
    }

    private void UpdateLowSpaceWarning()
    {
        if (!_services.Settings.FreeSpaceAlertEnabled)
        {
            LowSpaceWarning = null;
            return;
        }

        double threshold = _services.Settings.FreeSpaceAlertPercent / 100.0;
        var low = Drives
            .Where(d => d.Drive.DriveType == DriveType.Fixed && d.Drive.FreeFraction < threshold)
            .Select(d => $"{d.Letter} ({ByteSize.FormatPercent(d.Drive.FreeFraction)} livre)")
            .ToList();

        LowSpaceWarning = low.Count == 0
            ? null
            : $"{string.Join(", ", low)} — abaixo de {_services.Settings.FreeSpaceAlertPercent:0}%. " +
              "Escaneie o disco ou use a Limpeza para liberar espaço.";
    }
}
