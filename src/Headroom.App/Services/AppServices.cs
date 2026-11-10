using Headroom.Core.Settings;

namespace Headroom.App.Services;

/// <summary>Páginas do app (navegação lateral).</summary>
public enum PageKind
{
    Home,
    Overview,
    Treemap,
    FolderTree,
    LargestFiles,
    Duplicates,
    Cleanup,
    Health,
    Repairs,
    Performance,
    Programs,
    Registry,
    History,
    Settings,
}

/// <summary>Navegação entre páginas e início de scans (implementado pelo MainViewModel).</summary>
public interface INavigator
{
    void Navigate(PageKind page);

    /// <summary>Escaneia um ou mais discos/pastas, um após o outro (análise nova).</summary>
    Task StartScanAsync(IReadOnlyList<string> paths);

    /// <summary>Abre as análises salvas desses discos/pastas (os que não têm análise salva são escaneados).</summary>
    Task OpenSavedAsync(IReadOnlyList<string> paths);
}

/// <summary>Raiz de composição: serviços compartilhados por todos os ViewModels.</summary>
public sealed class AppServices
{
    public AppServices()
    {
        SettingsStore = new SettingsStore();
        Settings = SettingsStore.Load();
    }

    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public ScanSession Session { get; } = new();
    public DialogService Dialogs { get; } = new();
    public ShellService Shell { get; } = new();
    public ThemeService Theme { get; } = new();
    public bool IsAdministrator { get; } = Core.Scanning.DiskScanner.IsElevated();
    public Core.History.HistoryStore History { get; } = new();

    /// <summary>Catálogo de regras (recriado quando as configurações mudam).</summary>
    public Core.Cleanup.Catalog.RuleCatalog CreateRuleCatalog() =>
        Core.Cleanup.Catalog.RuleCatalog.Create(Settings, IsAdministrator);

    public INavigator Navigator { get; set; } = null!;

    /// <summary>Disparado depois que as configurações são salvas.</summary>
    public event EventHandler? SettingsChanged;

    public void SaveSettings(AppSettings settings)
    {
        SettingsStore.Save(settings);
        Settings = settings;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
