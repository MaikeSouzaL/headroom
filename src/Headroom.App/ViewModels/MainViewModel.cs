using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Services;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;
using Wpf.Ui.Controls;

namespace Headroom.App.ViewModels;

/// <summary>Item do menu lateral.</summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(PageKind page, string title, SymbolRegular icon, string group, bool requiresResult)
    {
        Page = page;
        Title = title;
        Icon = icon;
        Group = group;
        RequiresResult = requiresResult;
        _isEnabled = !requiresResult;
    }

    public PageKind Page { get; }
    public string Title { get; }
    public SymbolRegular Icon { get; }
    public string Group { get; }
    public bool RequiresResult { get; }

    [ObservableProperty]
    private bool _isEnabled;
}

/// <summary>Janela principal: menu lateral, página atual e fluxo de scan.</summary>
public sealed partial class MainViewModel : ObservableObject, INavigator
{
    private readonly AppServices _services;
    private readonly Dictionary<PageKind, object> _pages = new();
    private readonly Dictionary<PageKind, Func<object>> _factories;
    private bool _scanning;

    public MainViewModel(AppServices services)
    {
        _services = services;
        services.Navigator = this;

        _factories = new Dictionary<PageKind, Func<object>>
        {
            [PageKind.Home] = () => new HomeViewModel(services),
            [PageKind.Overview] = () => new OverviewViewModel(services),
            [PageKind.Treemap] = () => new TreemapViewModel(services),
            [PageKind.FolderTree] = () => new FolderTreeViewModel(services),
            [PageKind.LargestFiles] = () => new LargestFilesViewModel(services),
            [PageKind.Duplicates] = () => new DuplicatesViewModel(services),
            [PageKind.Cleanup] = () => new CleanupViewModel(services),
            [PageKind.Health] = () => new HealthViewModel(services),
            [PageKind.Repairs] = () => new RepairsViewModel(services),
            [PageKind.Performance] = () => new PerformanceViewModel(services),
            [PageKind.Programs] = () => new ProgramsViewModel(services),
            [PageKind.Registry] = () => new RegistryViewModel(services),
            [PageKind.History] = () => new HistoryViewModel(services),
            [PageKind.Settings] = () => new SettingsViewModel(services),
        };

        NavItems = new ObservableCollection<NavItem>
        {
            new(PageKind.Home, "Início", SymbolRegular.Home24, "Escanear", false),
            new(PageKind.Overview, "Visão geral", SymbolRegular.DataPie24, "Resultados", true),
            new(PageKind.Treemap, "Mapa de espaço", SymbolRegular.Grid24, "Resultados", true),
            new(PageKind.FolderTree, "Árvore de pastas", SymbolRegular.TextBulletListTree24, "Resultados", true),
            new(PageKind.LargestFiles, "Maiores arquivos", SymbolRegular.DocumentTable24, "Resultados", true),
            new(PageKind.Duplicates, "Duplicados", SymbolRegular.DocumentCopy24, "Resultados", true),
            new(PageKind.Cleanup, "Limpeza", SymbolRegular.Broom24, "Limpar e otimizar", false),
            new(PageKind.Health, "Saúde do PC", SymbolRegular.HeartPulse24, "Limpar e otimizar", false),
            new(PageKind.Repairs, "Reparos", SymbolRegular.Wrench24, "Limpar e otimizar", false),
            new(PageKind.Performance, "Desempenho", SymbolRegular.TopSpeed24, "Limpar e otimizar", false),
            new(PageKind.Programs, "Programas", SymbolRegular.Apps24, "Limpar e otimizar", false),
            new(PageKind.Registry, "Registro", SymbolRegular.Database24, "Limpar e otimizar", false),
            new(PageKind.History, "Histórico", SymbolRegular.History24, "Aplicativo", false),
            new(PageKind.Settings, "Configurações", SymbolRegular.Settings24, "Aplicativo", false),
        };

        var view = CollectionViewSource.GetDefaultView(NavItems);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.Group)));
        NavView = view;

        _services.Session.ResultChanged += (_, _) => UpdateNavAvailability();
        _services.Session.PropertyChanged += (_, e) =>
        {
            // Outro disco escolhido na barra "Disco analisado": a página atual se atualiza na hora.
            if (e.PropertyName == nameof(ScanSession.Result) && !_scanning)
            {
                UpdateResultBar();
                if (CurrentPage is IPageActivated page)
                {
                    page.OnActivated();
                }
            }
        };
        SelectedNavItem = NavItems[0];
    }

    public ObservableCollection<NavItem> NavItems { get; }

    public ICollectionView NavView { get; }

    public bool IsAdministrator => _services.IsAdministrator;

    public string PrivilegeText => IsAdministrator ? "Modo administrador" : "Sem administrador";

    [ObservableProperty]
    private NavItem? _selectedNavItem;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _statusText = "Pronto.";

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value is not null && !_scanning)
        {
            CurrentPage = GetPage(value.Page);
        }
    }

    public void Navigate(PageKind page)
    {
        var item = NavItems.First(n => n.Page == page);
        if (ReferenceEquals(SelectedNavItem, item))
        {
            CurrentPage = GetPage(page);
        }
        else
        {
            SelectedNavItem = item;
        }
    }

    public Task StartScanAsync(IReadOnlyList<string> paths) => RunAnalysesAsync(paths, preferSaved: false);

    public Task OpenSavedAsync(IReadOnlyList<string> paths) => RunAnalysesAsync(paths, preferSaved: true);

    /// <summary>Ao iniciar: reabre as últimas análises salvas (se a opção estiver ligada), sem escanear.</summary>
    public async Task OpenLastAnalysesOnStartAsync()
    {
        var settings = _services.Settings;
        if (!settings.SaveAnalyses || !settings.OpenLastAnalysisOnStart)
        {
            return;
        }

        var roots = await Task.Run(() => settings.LastAnalysisRoots.Where(r => ScanArchive.Find(r) is not null).ToList());
        if (roots.Count > 0)
        {
            await OpenSavedAsync(roots);
        }
    }

    /// <summary>Escaneia (ou abre o que está salvo) um disco por vez e mostra os resultados juntos.</summary>
    private async Task RunAnalysesAsync(IReadOnlyList<string> paths, bool preferSaved)
    {
        if (_scanning || paths.Count == 0)
        {
            return;
        }

        _scanning = true;
        ShowResultBar = false;
        var results = new List<ScanResult>();
        var failures = new List<(string Path, Exception Error)>();
        bool cancelled = false;
        try
        {
            // Um disco por vez: escanear dois discos ao mesmo tempo disputaria o mesmo controlador
            // e deixaria o progresso confuso.
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                var saved = preferSaved ? await Task.Run(() => ScanArchive.Find(path)) : null;
                var progressVm = new ScanProgressViewModel(path, i + 1, paths.Count, saved);
                CurrentPage = progressVm;
                StatusText = saved is not null
                    ? $"Abrindo a análise salva de {path} ({saved.ScannedAt:dd/MM/yyyy HH:mm})…"
                    : paths.Count == 1 ? $"Escaneando {path}…" : $"Escaneando {path} ({i + 1} de {paths.Count})…";
                try
                {
                    var result = await progressVm.RunAsync(_services.Settings);
                    results.Add(result);
                    if (!result.IsFromSavedAnalysis)
                    {
                        _ = Task.Run(() => _services.History.AddScan(result));
                        ScanCompleted?.Invoke(this, result);
                        _ = SaveAnalysisAsync(result);
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    failures.Add((path, ex));
                }
            }
        }
        finally
        {
            _scanning = false;
        }

        if (results.Count > 0)
        {
            foreach (var result in results)
            {
                WatchForChanges(result);
            }

            _services.Session.SetResults(results);
            RememberLastAnalysis(results);
            int opened = results.Count(r => r.IsFromSavedAnalysis);
            var elapsed = TimeSpan.FromTicks(results.Where(r => !r.IsFromSavedAnalysis).Sum(r => r.Elapsed.Ticks));
            StatusText = opened == results.Count
                ? $"{(opened == 1 ? "Análise salva aberta" : $"{opened} análises salvas abertas")} sem escanear " +
                  $"({ByteSize.FormatCount(results.Sum(r => r.FileCount))} arquivos). Use \"Nova análise\" para atualizar."
                : (results.Count == 1 ? "Scan concluído: " : $"Concluído ({results.Count} discos): ") +
                  $"{ByteSize.FormatCount(results.Sum(r => r.FileCount))} arquivos, " +
                  $"{ByteSize.Format(results.Sum(r => r.TotalBytes))}" +
                  (elapsed > TimeSpan.Zero ? $" em {ByteSize.FormatDuration(elapsed)}" : string.Empty) + "." +
                  (opened > 0 ? $" {opened} aberto(s) da análise salva." : string.Empty) +
                  (cancelled ? " O restante foi cancelado." : string.Empty);
            Navigate(PageKind.Overview);
        }
        else
        {
            StatusText = cancelled ? "Cancelado." : "O scan falhou.";
            Navigate(PageKind.Home);
        }

        await ReportFailuresAsync(failures);
    }

    /// <summary>"Nova análise" do disco exibido: escaneia só ele e troca o resultado, mantendo os outros discos.</summary>
    [RelayCommand]
    private async Task RescanCurrentAsync()
    {
        if (_scanning || _services.Session.Result is not { } current)
        {
            return;
        }

        var previousPage = SelectedNavItem;
        _scanning = true;
        ShowResultBar = false;
        var progressVm = new ScanProgressViewModel(current.RootPath);
        CurrentPage = progressVm;
        StatusText = $"Nova análise de {current.RootPath}…";
        ScanResult? fresh = null;
        Exception? failure = null;
        try
        {
            fresh = await progressVm.RunAsync(_services.Settings);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            _scanning = false;
        }

        if (fresh is not null)
        {
            WatchForChanges(fresh);
            _services.Session.Replace(current, fresh);
            RememberLastAnalysis(_services.Session.Results);
            _ = Task.Run(() => _services.History.AddScan(fresh));
            ScanCompleted?.Invoke(this, fresh);
            _ = SaveAnalysisAsync(fresh);
            StatusText = $"Nova análise de {fresh.RootPath}: {ByteSize.FormatCount(fresh.FileCount)} arquivos, " +
                         $"{ByteSize.Format(fresh.TotalBytes)} em {ByteSize.FormatDuration(fresh.Elapsed)}.";
        }
        else
        {
            StatusText = failure is null ? "Nova análise cancelada; continua a análise anterior." : "A nova análise falhou.";
        }

        // Volta para a página de resultado em que o usuário estava.
        CurrentPage = GetPage(previousPage?.Page ?? PageKind.Overview);
        UpdateResultBar();
        if (failure is not null)
        {
            await _services.Dialogs.ErrorAsync($"Não foi possível escanear {current.RootPath}", failure);
        }
    }

    private async Task ReportFailuresAsync(List<(string Path, Exception Error)> failures)
    {
        if (failures.Count == 1)
        {
            await _services.Dialogs.ErrorAsync($"Não foi possível abrir {failures[0].Path}", failures[0].Error);
        }
        else if (failures.Count > 1)
        {
            await _services.Dialogs.InfoAsync("Alguns discos não foram analisados",
                string.Join("\n\n", failures.Select(f => $"{f.Path}: {DialogService.FriendlyMessage(f.Error)}")));
        }
    }

    // ---------- Análises salvas ----------

    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<ScanResult, CancellationTokenSource> _pendingSaves = new(ReferenceEqualityComparer.Instance);

    /// <summary>Grava a análise em segundo plano (uma gravação por vez).</summary>
    private async Task SaveAnalysisAsync(ScanResult result)
    {
        if (!_services.Settings.SaveAnalyses)
        {
            return;
        }

        await _saveGate.WaitAsync();
        try
        {
            await ScanArchive.SaveAsync(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            App.LogCrash(ex);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>Depois de apagar algo pelo app, regrava a análise (3 s depois da última exclusão).</summary>
    private void WatchForChanges(ScanResult result) => result.Changed += (_, _) =>
    {
        if (_pendingSaves.Remove(result, out var previous))
        {
            previous.Cancel();
        }

        var cts = new CancellationTokenSource();
        _pendingSaves[result] = cts;
        _ = Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                _ = SaveAnalysisAsync(result);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    };

    private void RememberLastAnalysis(IEnumerable<ScanResult> results)
    {
        var roots = results.Select(r => r.RootPath).ToList();
        if (roots.SequenceEqual(_services.Settings.LastAnalysisRoots, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var settings = _services.Settings.Clone();
            settings.LastAnalysisRoots = roots;
            _services.SaveSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.LogCrash(ex);
        }
    }

    public ScanSession Session => _services.Session;

    /// <summary>Barra das telas de resultado: data da análise, "Nova análise" e troca de disco.</summary>
    [ObservableProperty]
    private bool _showResultBar;

    [ObservableProperty]
    private string _resultInfoText = string.Empty;

    [ObservableProperty]
    private bool _resultIsSaved;

    partial void OnCurrentPageChanged(object? oldValue, object? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue) && oldValue is IPageDeactivated leaving)
        {
            leaving.OnDeactivated();
        }

        UpdateResultBar();
    }

    private void UpdateResultBar()
    {
        var result = _services.Session.Result;
        ShowResultBar = !_scanning && result is not null && SelectedNavItem?.RequiresResult == true && CurrentPage is not ScanProgressViewModel;
        ResultIsSaved = result?.IsFromSavedAnalysis == true;
        ResultInfoText = result is null
            ? string.Empty
            : $"Análise de {result.RootPath} feita em {result.StartedAt:dd/MM/yyyy HH:mm}" +
              (result.IsFromSavedAnalysis ? " (salva; o que mudou no disco depois disso não aparece)" : string.Empty);
    }

    /// <summary>Disparado ao fim de cada scan (usado pelo histórico).</summary>
    public event EventHandler<Core.Scanning.ScanResult>? ScanCompleted;

    [RelayCommand]
    private async Task RestartAsAdminAsync()
    {
        if (!_services.Shell.RestartAsAdministrator())
        {
            await _services.Dialogs.InfoAsync("Elevação cancelada",
                "O Headroom continua sem privilégios de administrador.");
        }
    }

    private object GetPage(PageKind page)
    {
        if (!_pages.TryGetValue(page, out var vm))
        {
            vm = _factories[page]();
            _pages[page] = vm;
        }

        if (vm is IPageActivated activated)
        {
            activated.OnActivated();
        }

        return vm;
    }

    private void UpdateNavAvailability()
    {
        bool has = _services.Session.HasResult;
        foreach (var item in NavItems.Where(n => n.RequiresResult))
        {
            item.IsEnabled = has;
        }
    }
}

/// <summary>Páginas que querem ser avisadas quando ficam visíveis.</summary>
public interface IPageActivated
{
    void OnActivated();
}

/// <summary>Páginas que querem ser avisadas quando deixam de estar visíveis (ex.: parar um monitor ao vivo).</summary>
public interface IPageDeactivated
{
    void OnDeactivated();
}
