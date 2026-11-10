using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Helpers;
using Headroom.App.Services;
using Headroom.Core.Cleanup;
using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Formatting;
using Headroom.Core.Programs;

namespace Headroom.App.ViewModels;

/// <summary>Linha de programa instalado.</summary>
public sealed partial class ProgramRowViewModel : ObservableObject
{
    public ProgramRowViewModel(InstalledProgram program)
    {
        Program = program;
    }

    public InstalledProgram Program { get; }
    public string Name => Program.Name;
    public string Publisher => Program.Publisher ?? string.Empty;
    public string Version => Program.Version ?? string.Empty;
    public long Size => Program.SizeBytes;
    public string SizeText => Program.SizeBytes > 0 ? ByteSize.Format(Program.SizeBytes) + (Program.MeasuredSizeBytes is null ? " *" : string.Empty) : "—";
    public DateTime? InstallDate => Program.InstallDate;
    public DateTime? LastUsed => Program.LastUsed;
    public string Folder => Program.ProbableFolder ?? string.Empty;
    public string Scope => (Program.IsUserInstall ? "Usuário" : "Todos") + (Program.Is32Bit ? " · 32 bits" : string.Empty);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(LastUsed));
    }
}

/// <summary>Linha de app da Store.</summary>
public sealed partial class StoreAppRowViewModel : ObservableObject
{
    public StoreAppRowViewModel(StoreApp app)
    {
        App = app;
    }

    public StoreApp App { get; }
    public string Name => App.DisplayName;
    public string Package => App.Name;
    public string Version => App.Version ?? string.Empty;
    public long Size => App.SizeBytes;
    public string SizeText => App.SizeBytes > 0 ? ByteSize.Format(App.SizeBytes) : "—";
    public bool IsBloatware => App.IsBloatware;
    public bool CanRemove => !App.NonRemovable;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Linha de recurso opcional.</summary>
public sealed partial class CapabilityRowViewModel : ObservableObject
{
    public CapabilityRowViewModel(WindowsCapability capability)
    {
        Capability = capability;
    }

    public WindowsCapability Capability { get; }
    public string Name => Capability.DisplayName;
    public string Kind => Capability.IsLanguage ? $"Idioma ({Capability.LanguageTag})" : "Recurso opcional";
    public bool IsUnusedLanguage => Capability.IsUnusedLanguage;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Pasta órfã (resto de programa).</summary>
public sealed partial class OrphanRowViewModel : ObservableObject, IFileItem
{
    public OrphanRowViewModel(CleanupItem item)
    {
        Item = item;
    }

    public CleanupItem Item { get; }
    public string FullPath => Item.Path;
    public string Name => Item.Name;
    public bool IsDirectory => true;
    public long Size => Item.Size;
    public Core.Scanning.ScanNode? Node => null;
    public string SizeText => ByteSize.Format(Item.Size);
    public DateTime Modified => Item.LastModified;
    public string? Reason => Item.Reason;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Rastro de um programa desinstalado (pasta, atalho, registro, tarefa ou serviço).</summary>
public sealed partial class LeftoverRowViewModel : ObservableObject
{
    public LeftoverRowViewModel(ProgramLeftover leftover)
    {
        Leftover = leftover;
        _isSelected = leftover.IsSelected;
    }

    public ProgramLeftover Leftover { get; }
    public string Kind => Leftover.KindText;
    public string Display => Leftover.Display;
    public string Reason => Leftover.Reason;
    public long Size => Leftover.Size;
    public string SizeText => Leftover.Size > 0 ? ByteSize.Format(Leftover.Size) : string.Empty;
    public bool NeedsReview => Leftover.Confidence == LeftoverConfidence.Medium;
    public bool RequiresAdmin => Leftover.RequiresAdmin;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => Leftover.IsSelected = value;
}

/// <summary>Programas instalados, apps da Store, recursos opcionais e restos de programas.</summary>
public sealed partial class ProgramsViewModel : FileActionsViewModel, IPageActivated
{
    public const int TabInstalled = 0;
    public const int TabCompleteUninstall = 1;

    private bool _loaded;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _waitCts;
    private List<ProgramRowViewModel> _allPrograms = new();
    private ProgramFootprint? _footprint;
    private bool _forcedRemoval;
    private string? _lastLeftoverLog;

    public ProgramsViewModel(AppServices services) : base(services)
    {
        Leftovers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLeftovers));
    }

    public ObservableCollection<LeftoverRowViewModel> Leftovers { get; } = new();
    public bool HasLeftovers => Leftovers.Count > 0;

    [ObservableProperty] private int _selectedTab;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLeftoversNowCommand))]
    private bool _isWaitingUninstall;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLeftoversNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveLeftoversCommand))]
    private bool _isWorkingOnLeftovers;

    [ObservableProperty] private string _uninstallTitle = "Desinstalação completa";

    [ObservableProperty]
    private string _leftoverStatus =
        "Escolha um programa na aba Instalados e clique em Desinstalar. Quando o desinstalador dele terminar, o Headroom " +
        "procura o que ficou para trás (pastas, atalhos, registro, itens de inicialização, tarefas agendadas e serviços) e " +
        "mostra tudo aqui para você revisar antes de apagar.";

    [ObservableProperty] private string? _leftoverResult;
    [ObservableProperty] private bool _hasLeftoverLog;

    public BulkObservableCollection<ProgramRowViewModel> Programs { get; } = new();
    public ObservableCollection<StoreAppRowViewModel> StoreApps { get; } = new();
    public ObservableCollection<CapabilityRowViewModel> Capabilities { get; } = new();
    public ObservableCollection<OrphanRowViewModel> Orphans { get; } = new();
    public bool IsAdministrator => Services.IsAdministrator;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private ProgramRowViewModel? _selectedProgram;
    [ObservableProperty] private string _storeStatus = "Clique em \"Carregar\" para listar os apps da Microsoft Store.";
    [ObservableProperty] private string _capabilityStatus = "Clique em \"Carregar\" para listar os recursos opcionais instalados (requer administrador).";
    [ObservableProperty] private string _orphanStatus = "Clique em \"Procurar\" para encontrar pastas de programas que já foram desinstalados.";

    partial void OnSearchChanged(string value) => ApplyFilter();

    public async void OnActivated()
    {
        if (!_loaded)
        {
            _loaded = true;
            await LoadProgramsAsync();
        }
    }

    [RelayCommand]
    private async Task LoadProgramsAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var programs = await Task.Run(InstalledProgramsService.List);
            _allPrograms = programs.Select(p => new ProgramRowViewModel(p)).ToList();
            ApplyFilter();
            StatusText = $"{programs.Count} programas instalados. Medindo o tamanho real das pastas…";

            var lookup = _allPrograms.ToDictionary(r => r.Program);
            var progress = new Progress<InstalledProgram>(p => lookup[p].Refresh());
            await Task.Run(() => InstalledProgramsService.MeasureAll(programs, IsAdministrator, progress, _cts.Token));
            ApplyFilter();
            StatusText = $"{programs.Count} programas instalados, {ByteSize.Format(programs.Sum(p => p.SizeBytes))} no total. " +
                         "* = tamanho informado pelo instalador (pasta não encontrada)." +
                         (IsAdministrator ? string.Empty : " Rode como administrador para estimar o último uso (Prefetch).");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ErrorAsync("Não foi possível listar os programas", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        string s = Search.Trim();
        Programs.ReplaceAll(_allPrograms
            .Where(p => s.Length == 0 || p.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || p.Publisher.Contains(s, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Size));
    }

    /// <summary>
    /// Desinstalação completa: abre o desinstalador oficial, espera ele terminar e procura os rastros.
    /// Se o desinstalador não existe mais, oferece remover os rastros direto ("remoção forçada").
    /// </summary>
    [RelayCommand]
    private async Task UninstallAsync(ProgramRowViewModel? row)
    {
        row ??= SelectedProgram;
        if (row is null || IsWaitingUninstall || IsWorkingOnLeftovers)
        {
            return;
        }

        bool hasUninstaller = InstalledProgramsService.HasWorkingUninstaller(row.Program);
        bool confirmed = hasUninstaller
            ? await Services.Dialogs.ConfirmAsync("Desinstalar",
                $"Desinstalar \"{row.Name}\"?\n\nO desinstalador do próprio programa vai abrir. Quando ele terminar, o Headroom " +
                "procura pastas, atalhos, registro, itens de inicialização, tarefas e serviços que ficaram para trás e mostra a " +
                "lista para você revisar antes de apagar.", "Desinstalar")
            : await Services.Dialogs.ConfirmAsync("Desinstalador não encontrado",
                $"O desinstalador de \"{row.Name}\" não existe mais, então a desinstalação normal não é possível.\n\n" +
                "Procurar e mostrar os rastros do programa (pastas, registro, atalhos) para remover?", "Procurar rastros");
        if (!confirmed)
        {
            return;
        }

        _footprint = await Task.Run(() => ProgramFootprint.From(row.Program));
        _forcedRemoval = !hasUninstaller;
        ScanLeftoversNowCommand.NotifyCanExecuteChanged();
        UninstallTitle = $"Desinstalação completa: {row.Name}";
        Leftovers.Clear();
        LeftoverResult = null;
        HasLeftoverLog = false;
        SelectedTab = TabCompleteUninstall;

        if (!hasUninstaller)
        {
            await FindLeftoversAsync();
            return;
        }

        System.Diagnostics.Process? uninstaller;
        try
        {
            uninstaller = InstalledProgramsService.Uninstall(row.Program);
        }
        catch (Exception ex)
        {
            LeftoverStatus = "O desinstalador não abriu.";
            await Services.Dialogs.ErrorAsync("Não foi possível desinstalar", ex);
            return;
        }

        IsWaitingUninstall = true;
        LeftoverStatus = $"Esperando o desinstalador de \"{row.Name}\" terminar… Siga as instruções na janela dele. " +
                         "Se ele pedir para reiniciar, escolha reiniciar depois e volte aqui.";
        _waitCts = new CancellationTokenSource();
        try
        {
            bool finished = await WaitForUninstallAsync(uninstaller, _waitCts.Token);
            if (!finished)
            {
                LeftoverStatus = $"\"{row.Name}\" ainda aparece como instalado: a desinstalação foi cancelada ou só termina depois de " +
                                 "reiniciar. Se ela terminou, clique em \"Procurar rastros agora\".";
                return;
            }
        }
        catch (OperationCanceledException)
        {
            LeftoverStatus = "Espera cancelada. Clique em \"Procurar rastros agora\" quando a desinstalação terminar.";
            return;
        }
        finally
        {
            IsWaitingUninstall = false;
        }

        await FindLeftoversAsync();
    }

    /// <summary>Verdadeiro quando a entrada do programa sumiu do registro (desinstalação concluída).</summary>
    private async Task<bool> WaitForUninstallAsync(System.Diagnostics.Process? uninstaller, CancellationToken ct)
    {
        DateTime? idleSince = null;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            if (!ProgramLeftoverScanner.IsStillRegistered(_footprint!))
            {
                // Dá um tempo para o desinstalador apagar os últimos arquivos dele.
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                return true;
            }

            bool running = (uninstaller is { HasExited: false }) || await Task.Run(InstalledProgramsService.IsAnyUninstallerRunning, ct);
            if (running)
            {
                idleSince = null;
            }
            else if ((idleSince ??= DateTime.Now) < DateTime.Now.AddSeconds(-15))
            {
                return false;
            }
        }
    }

    private bool CanScanLeftoversNow() => _footprint is not null && !IsWorkingOnLeftovers;

    [RelayCommand(CanExecute = nameof(CanScanLeftoversNow))]
    private async Task ScanLeftoversNowAsync()
    {
        _waitCts?.Cancel();
        if (_footprint is not null && !_forcedRemoval && ProgramLeftoverScanner.IsStillRegistered(_footprint) &&
            !await Services.Dialogs.ConfirmAsync("O programa ainda aparece como instalado",
                "Procurar rastros agora pode listar arquivos que o programa ainda usa. Os itens continuam desmarcáveis e nada é apagado " +
                "sem confirmação.\n\nProcurar mesmo assim?", "Procurar"))
        {
            return;
        }

        await FindLeftoversAsync();
    }

    [RelayCommand]
    private void CancelUninstallWait() => _waitCts?.Cancel();

    private async Task FindLeftoversAsync()
    {
        if (_footprint is null)
        {
            return;
        }

        IsWorkingOnLeftovers = true;
        LeftoverStatus = $"Procurando rastros de \"{_footprint.Name}\"…";
        try
        {
            var programs = await Task.Run(InstalledProgramsService.List);
            var leftovers = await ProgramLeftoverScanner.FindAsync(_footprint, programs);
            Leftovers.Clear();
            foreach (var item in leftovers)
            {
                Leftovers.Add(new LeftoverRowViewModel(item));
            }

            int review = leftovers.Count(l => l.Confidence == LeftoverConfidence.Medium);
            LeftoverStatus = leftovers.Count == 0
                ? $"Nenhum rastro de \"{_footprint.Name}\" encontrado: o desinstalador limpou tudo."
                : $"{leftovers.Count} rastro(s) de \"{_footprint.Name}\", {ByteSize.Format(leftovers.Sum(l => l.Size))}. " +
                  "Os identificados com segurança já vêm marcados" +
                  (review > 0 ? $"; {review} marcado(s) como \"Revisar\" só têm nome parecido e vêm desmarcados." : ".") +
                  " Arquivos vão para a Lixeira e o registro ganha um backup que pode ser desfeito na tela Registro.";

            // A lista de instalados também muda depois de desinstalar.
            _allPrograms = programs.Select(p => new ProgramRowViewModel(p)).ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            LeftoverStatus = "Não foi possível procurar os rastros.";
            await Services.Dialogs.ErrorAsync("Falha ao procurar rastros", ex);
        }
        finally
        {
            IsWorkingOnLeftovers = false;
        }
    }

    private bool CanRemoveLeftovers() => !IsWorkingOnLeftovers;

    [RelayCommand(CanExecute = nameof(CanRemoveLeftovers))]
    private async Task RemoveLeftoversAsync()
    {
        var selected = Leftovers.Where(l => l.IsSelected).ToList();
        if (selected.Count == 0 || _footprint is null)
        {
            return;
        }

        long size = selected.Sum(l => l.Size);
        int needAdmin = selected.Count(l => l.RequiresAdmin && l.Leftover.Kind is not (LeftoverKind.Folder or LeftoverKind.Shortcut));
        if (!await Services.Dialogs.ConfirmAsync("Remover rastros",
                $"Remover {selected.Count} rastro(s) de \"{_footprint.Name}\"" + (size > 0 ? $" ({ByteSize.Format(size)})" : string.Empty) + "?\n\n" +
                "• Pastas e atalhos vão para a Lixeira.\n• Chaves do registro são exportadas para .reg antes (dá para desfazer na tela Registro).\n" +
                "• Tarefas e serviços têm uma cópia guardada antes de sair." +
                (needAdmin > 0 && !IsAdministrator ? $"\n\n{needAdmin} item(ns) exigem administrador e serão pulados." : string.Empty),
                "Remover"))
        {
            return;
        }

        IsWorkingOnLeftovers = true;
        try
        {
            var result = await ProgramLeftoverRemover.RemoveAsync(selected.Select(s => s.Leftover).ToList(), _footprint.Name, IsAdministrator);
            _lastLeftoverLog = result.LogPath;
            HasLeftoverLog = result.LogPath is not null;
            LeftoverResult = result.Summary +
                             (result.Skipped.Count > 0 ? "\nPulados: " + string.Join("; ", result.Skipped.Take(5)) : string.Empty) +
                             (result.Errors.Count > 0 ? "\nErros: " + string.Join("; ", result.Errors.Take(5)) : string.Empty);

            foreach (var row in selected)
            {
                bool gone = row.Leftover.Kind switch
                {
                    LeftoverKind.Folder => !Directory.Exists(row.Leftover.Path),
                    LeftoverKind.Shortcut => !File.Exists(row.Leftover.Path),
                    _ => !result.Skipped.Any(s => s.StartsWith(row.Display, StringComparison.OrdinalIgnoreCase)) &&
                         !result.Errors.Any(e => e.Contains(row.Leftover.Path, StringComparison.OrdinalIgnoreCase)),
                };
                if (gone)
                {
                    Leftovers.Remove(row);
                }
            }

            LeftoverStatus = Leftovers.Count == 0
                ? $"Pronto: \"{_footprint.Name}\" foi desinstalado e os rastros marcados foram removidos."
                : $"Ficaram {Leftovers.Count} rastro(s) de \"{_footprint.Name}\" (desmarcados, pulados ou com erro). Veja o resultado abaixo.";
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ErrorAsync("Não foi possível remover os rastros", ex);
        }
        finally
        {
            IsWorkingOnLeftovers = false;
        }
    }

    [RelayCommand]
    private void SelectRecommendedLeftovers()
    {
        foreach (var row in Leftovers)
        {
            row.IsSelected = !row.NeedsReview;
        }
    }

    [RelayCommand]
    private void ClearLeftoverSelection()
    {
        foreach (var row in Leftovers)
        {
            row.IsSelected = false;
        }
    }

    [RelayCommand]
    private void OpenLeftoverLog()
    {
        if (_lastLeftoverLog is { } log && File.Exists(log))
        {
            Services.Shell.OpenUrlOrFile(log);
        }
    }

    [RelayCommand]
    private void OpenProgramFolder(ProgramRowViewModel? row)
    {
        if (row?.Program.ProbableFolder is { } folder)
        {
            Services.Shell.OpenInExplorer(folder);
        }
    }

    [RelayCommand]
    private async Task LoadStoreAppsAsync()
    {
        IsBusy = true;
        try
        {
            StoreStatus = "Carregando apps da Store…";
            var apps = await StoreAndFeaturesService.ListStoreAppsAsync(CancellationToken.None);
            StoreApps.Clear();
            foreach (var app in apps)
            {
                StoreApps.Add(new StoreAppRowViewModel(app));
            }

            int bloat = apps.Count(a => a.IsBloatware);
            StoreStatus = $"{apps.Count} apps; {bloat} costumam vir pré-instalados e podem ser removidos (reinstale pela Store quando quiser).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectBloatware()
    {
        foreach (var app in StoreApps)
        {
            app.IsSelected = app.IsBloatware && app.CanRemove;
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedAppsAsync()
    {
        var selected = StoreApps.Where(a => a.IsSelected && a.CanRemove).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Remover apps",
                $"Remover {selected.Count} app(s) para o seu usuário?\n\n{string.Join("\n", selected.Select(a => "• " + a.Name))}", "Remover", danger: true))
        {
            return;
        }

        IsBusy = true;
        var failures = new List<string>();
        foreach (var app in selected)
        {
            StoreStatus = $"Removendo {app.Name}…";
            var result = await StoreAndFeaturesService.RemoveStoreAppAsync(app.App, CancellationToken.None);
            if (result.Output.Contains("OK", StringComparison.Ordinal))
            {
                StoreApps.Remove(app);
            }
            else
            {
                failures.Add($"{app.Name}: {result.Error.Trim()}");
            }
        }

        IsBusy = false;
        StoreStatus = failures.Count == 0 ? $"{selected.Count} app(s) removido(s)." : $"Alguns apps não foram removidos: {string.Join("; ", failures)}";
    }

    [RelayCommand]
    private async Task LoadCapabilitiesAsync()
    {
        if (!IsAdministrator)
        {
            await Services.Dialogs.InfoAsync("Requer administrador", "Listar e remover recursos opcionais exige executar como administrador.");
            return;
        }

        IsBusy = true;
        try
        {
            CapabilityStatus = "Consultando recursos opcionais (pode levar um minuto)…";
            var caps = await StoreAndFeaturesService.ListCapabilitiesAsync(CancellationToken.None);
            Capabilities.Clear();
            foreach (var cap in caps)
            {
                Capabilities.Add(new CapabilityRowViewModel(cap) { IsSelected = false });
            }

            CapabilityStatus = $"{caps.Count} recursos instalados; {caps.Count(c => c.IsUnusedLanguage)} pacote(s) de idioma fora da sua lista de idiomas.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedCapabilitiesAsync()
    {
        var selected = Capabilities.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Remover recursos opcionais (Avançado)",
                $"Remover {selected.Count} recurso(s)? Podem ser reinstalados em Configurações > Aplicativos > Recursos opcionais.\n\n" +
                string.Join("\n", selected.Select(c => "• " + c.Name)), "Remover", danger: true))
        {
            return;
        }

        IsBusy = true;
        foreach (var cap in selected)
        {
            CapabilityStatus = $"Removendo {cap.Name}…";
            var result = await StoreAndFeaturesService.RemoveCapabilityAsync(cap.Capability, CancellationToken.None);
            if (result.Output.Contains("OK", StringComparison.Ordinal))
            {
                Capabilities.Remove(cap);
            }
        }

        IsBusy = false;
        CapabilityStatus = "Concluído. Alguns recursos só somem depois de reiniciar.";
    }

    [RelayCommand]
    private async Task FindOrphansAsync()
    {
        IsBusy = true;
        try
        {
            OrphanStatus = "Procurando restos de programas…";
            var programs = _allPrograms.Count > 0 ? _allPrograms.Select(p => p.Program).ToList() : await Task.Run(InstalledProgramsService.List);
            var orphans = await OrphanFolderDetector.FindAsync(programs, CancellationToken.None);
            Orphans.Clear();
            foreach (var orphan in orphans)
            {
                Orphans.Add(new OrphanRowViewModel(orphan));
            }

            OrphanStatus = orphans.Count == 0
                ? "Nenhuma pasta órfã encontrada."
                : $"{orphans.Count} pasta(s), {ByteSize.Format(orphans.Sum(o => o.Size))}. Leia o motivo de cada uma antes de marcar.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedOrphansAsync()
    {
        var selected = Orphans.Where(o => o.IsSelected).Cast<IFileItem>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var result = await DeleteItemsAsync(selected, DeletionMode.RecycleBin);
        if (result is not null)
        {
            foreach (var row in Orphans.Where(o => o.IsSelected && !Directory.Exists(o.FullPath)).ToList())
            {
                Orphans.Remove(row);
            }
        }
    }
}
