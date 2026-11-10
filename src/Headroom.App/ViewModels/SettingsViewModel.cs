using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Services;
using Headroom.Core.Cleanup;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;
using Headroom.Core.Scheduling;
using Headroom.Core.Settings;

namespace Headroom.App.ViewModels;

public sealed record Choice<T>(T Value, string Name);

/// <summary>Configurações: tema, idioma, tamanhos, pastas, agendamento e alertas.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IPageActivated
{
    private readonly AppServices _services;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        Load();
    }

    public IReadOnlyList<Choice<AppTheme>> Themes { get; } =
    [
        new(AppTheme.System, "Seguir o Windows"), new(AppTheme.Light, "Claro"), new(AppTheme.Dark, "Escuro"),
    ];

    public IReadOnlyList<Choice<string>> Languages { get; } = [new("pt-BR", "Português (Brasil)")];

    public IReadOnlyList<Choice<ScheduleFrequency>> Frequencies { get; } =
    [
        new(ScheduleFrequency.Daily, "Diária"), new(ScheduleFrequency.Weekly, "Semanal (segunda-feira)"), new(ScheduleFrequency.Monthly, "Mensal (dia 1)"),
    ];

    public IReadOnlyList<CleanupProfile> Profiles => CleanupProfiles.All(_services.Settings.CustomProfileRuleIds);

    public ObservableCollection<string> ExcludedPaths { get; } = new();
    public ObservableCollection<string> DevProjectRoots { get; } = new();
    public ObservableCollection<string> CompressionFolders { get; } = new();

    [ObservableProperty] private Choice<AppTheme>? _selectedTheme;
    [ObservableProperty] private Choice<string>? _selectedLanguage;
    [ObservableProperty] private double _largeFileMinimumMb;
    [ObservableProperty] private double _topFilesCount;
    [ObservableProperty] private double _duplicateMinimumMb;
    [ObservableProperty] private double _staleProjectDays;
    [ObservableProperty] private double _oldDownloadsDays;
    [ObservableProperty] private double _oldLargeFileMinimumMb;
    [ObservableProperty] private double _oldLargeFileMonths;
    [ObservableProperty] private double _oldScreenshotsDays;
    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _freeSpaceAlertEnabled;
    [ObservableProperty] private double _freeSpaceAlertPercent;
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private Choice<ScheduleFrequency>? _selectedFrequency;
    [ObservableProperty] private string _scheduleTime = "10:00";
    [ObservableProperty] private CleanupProfile? _scheduleProfile;
    [ObservableProperty] private string _scheduleStatus = string.Empty;
    [ObservableProperty] private string _saveStatus = string.Empty;
    [ObservableProperty] private bool _saveAnalyses;
    [ObservableProperty] private bool _openLastAnalysisOnStart;
    [ObservableProperty] private string _savedAnalysesInfo = string.Empty;

    private void UpdateSavedAnalysesInfo()
    {
        var saved = ScanArchive.List();
        SavedAnalysesInfo = saved.Count == 0
            ? $"Nenhuma análise salva. Ficam em {ScanArchive.DefaultDirectory}."
            : $"{saved.Count} análise(s) salva(s) ocupando {ByteSize.Format(saved.Sum(s => s.FileSizeBytes))}: " +
              string.Join(", ", saved.Select(s => $"{s.RootPath} ({s.ScannedAt:dd/MM HH:mm})")) + ".";
    }

    [RelayCommand]
    private async Task DeleteSavedAnalysesAsync()
    {
        var saved = ScanArchive.List();
        if (saved.Count == 0)
        {
            return;
        }

        if (!await _services.Dialogs.ConfirmAsync("Apagar análises salvas",
                $"Apagar {saved.Count} análise(s) salva(s) ({ByteSize.Format(saved.Sum(s => s.FileSizeBytes))})? " +
                "Seus arquivos não são afetados; só será preciso escanear de novo.", "Apagar"))
        {
            return;
        }

        foreach (var info in saved)
        {
            try
            {
                File.Delete(info.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _services.Dialogs.ErrorAsync("Não foi possível apagar uma análise salva", ex);
            }
        }

        UpdateSavedAnalysesInfo();
    }

    public string DataFolder => AppPaths.DataDirectory;
    public bool IsAdministrator => _services.IsAdministrator;

    public async void OnActivated()
    {
        bool cleanup = await ScheduledTasks.ExistsAsync(ScheduledTasks.CleanupTaskName);
        bool alert = await ScheduledTasks.ExistsAsync(ScheduledTasks.SpaceAlertTaskName);
        ScheduleStatus = $"Limpeza agendada: {(cleanup ? "ativa" : "não configurada")}. Verificação de espaço livre: {(alert ? "ativa (a cada 4 horas)" : "não configurada")}.";
        UpdateSavedAnalysesInfo();
    }

    private void Load()
    {
        var s = _services.Settings;
        SelectedTheme = Themes.First(t => t.Value == s.Theme);
        SelectedLanguage = Languages.FirstOrDefault(l => l.Value == s.Language) ?? Languages[0];
        LargeFileMinimumMb = s.LargeFileMinimumBytes / (double)ByteSize.MB;
        TopFilesCount = s.TopFilesCount;
        DuplicateMinimumMb = s.DuplicateMinimumBytes / (double)ByteSize.MB;
        StaleProjectDays = s.StaleProjectDays;
        OldDownloadsDays = s.OldDownloadsDays;
        OldLargeFileMinimumMb = s.OldLargeFileMinimumBytes / (double)ByteSize.MB;
        OldLargeFileMonths = s.OldLargeFileMonths;
        OldScreenshotsDays = s.OldScreenshotsDays;
        CreateRestorePoint = s.CreateRestorePointBeforeAdvanced;
        SaveAnalyses = s.SaveAnalyses;
        OpenLastAnalysisOnStart = s.OpenLastAnalysisOnStart;
        FreeSpaceAlertEnabled = s.FreeSpaceAlertEnabled;
        FreeSpaceAlertPercent = s.FreeSpaceAlertPercent;
        ScheduleEnabled = s.ScheduleEnabled;
        SelectedFrequency = Frequencies.First(f => f.Value == s.ScheduleFrequency);
        ScheduleTime = s.ScheduleTime;
        ScheduleProfile = Profiles.FirstOrDefault(p => p.Id == s.ScheduleProfileId) ?? Profiles[0];
        Fill(ExcludedPaths, s.ExcludedPaths);
        Fill(DevProjectRoots, s.DevProjectRoots);
        Fill(CompressionFolders, s.CompressionFolders);
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var v in values)
        {
            target.Add(v);
        }
    }

    partial void OnSelectedThemeChanged(Choice<AppTheme>? value)
    {
        if (value is not null)
        {
            _services.Theme.Apply(value.Value);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var s = _services.Settings.Clone();
        s.Theme = SelectedTheme?.Value ?? AppTheme.System;
        s.Language = SelectedLanguage?.Value ?? "pt-BR";
        s.LargeFileMinimumBytes = (long)(Math.Max(0, LargeFileMinimumMb) * ByteSize.MB);
        s.TopFilesCount = (int)Math.Clamp(TopFilesCount, 10, 100_000);
        s.DuplicateMinimumBytes = (long)(Math.Max(0, DuplicateMinimumMb) * ByteSize.MB);
        s.StaleProjectDays = (int)Math.Clamp(StaleProjectDays, 1, 3650);
        s.OldDownloadsDays = (int)Math.Clamp(OldDownloadsDays, 1, 3650);
        s.OldLargeFileMinimumBytes = (long)(Math.Max(1, OldLargeFileMinimumMb) * ByteSize.MB);
        s.OldLargeFileMonths = (int)Math.Clamp(OldLargeFileMonths, 1, 240);
        s.OldScreenshotsDays = (int)Math.Clamp(OldScreenshotsDays, 1, 3650);
        s.CreateRestorePointBeforeAdvanced = CreateRestorePoint;
        s.SaveAnalyses = SaveAnalyses;
        s.OpenLastAnalysisOnStart = OpenLastAnalysisOnStart;
        s.FreeSpaceAlertEnabled = FreeSpaceAlertEnabled;
        s.FreeSpaceAlertPercent = Math.Clamp(FreeSpaceAlertPercent, 1, 50);
        s.ScheduleEnabled = ScheduleEnabled;
        s.ScheduleFrequency = SelectedFrequency?.Value ?? ScheduleFrequency.Weekly;
        s.ScheduleTime = TimeOnly.TryParse(ScheduleTime, out var t) ? t.ToString("HH:mm") : "10:00";
        s.ScheduleProfileId = ScheduleProfile?.Id ?? CleanupProfiles.Quick;
        s.ExcludedPaths = ExcludedPaths.ToList();
        s.DevProjectRoots = DevProjectRoots.ToList();
        s.CompressionFolders = CompressionFolders.ToList();

        try
        {
            _services.SaveSettings(s);
            SaveStatus = $"Configurações salvas às {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Não foi possível salvar", ex);
        }
    }

    [RelayCommand]
    private async Task ApplyScheduleAsync()
    {
        await SaveAsync();
        try
        {
            if (ScheduleEnabled)
            {
                var result = await ScheduledTasks.CreateCleanupAsync(SelectedFrequency?.Value ?? ScheduleFrequency.Weekly, ScheduleTime,
                    ScheduleProfile?.Id ?? CleanupProfiles.Quick, highestPrivileges: IsAdministrator);
                ScheduleStatus = result.Success
                    ? $"Limpeza agendada criada no Agendador de Tarefas ({SelectedFrequency?.Name}, às {ScheduleTime}, perfil {ScheduleProfile?.Name})." +
                      (IsAdministrator ? " Roda com privilégios elevados." : " Roda sem privilégios elevados (itens do sistema serão pulados).")
                    : "Não foi possível criar a tarefa: " + result.Combined.Trim();
            }
            else
            {
                await ScheduledTasks.DeleteAsync(ScheduledTasks.CleanupTaskName);
                ScheduleStatus = "Limpeza agendada removida.";
            }

            if (FreeSpaceAlertEnabled)
            {
                await ScheduledTasks.CreateSpaceAlertAsync();
            }
            else
            {
                await ScheduledTasks.DeleteAsync(ScheduledTasks.SpaceAlertTaskName);
            }
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Agendamento", ex);
        }
    }

    [RelayCommand]
    private void AddExcludedPath() => AddFolder(ExcludedPaths, "Pasta que o scanner deve ignorar");

    [RelayCommand]
    private void AddDevRoot() => AddFolder(DevProjectRoots, "Pasta onde ficam seus projetos");

    [RelayCommand]
    private void AddCompressionFolder() => AddFolder(CompressionFolders, "Pasta para compressão NTFS");

    [RelayCommand]
    private void RemoveExcludedPath(string? path) => Remove(ExcludedPaths, path);

    [RelayCommand]
    private void RemoveDevRoot(string? path) => Remove(DevProjectRoots, path);

    [RelayCommand]
    private void RemoveCompressionFolder(string? path) => Remove(CompressionFolders, path);

    private void AddFolder(ObservableCollection<string> list, string title)
    {
        string? folder = _services.Dialogs.PickFolder(title);
        if (folder is not null && !list.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(folder);
        }
    }

    private static void Remove(ObservableCollection<string> list, string? path)
    {
        if (path is not null)
        {
            list.Remove(path);
        }
    }

    [RelayCommand]
    private void OpenLogs() => _services.Shell.OpenInExplorer(AppPaths.EnsureDirectory(AppPaths.LogsDirectory));

    [RelayCommand]
    private void OpenRules() => _services.Shell.OpenInExplorer(AppPaths.EnsureDirectory(AppPaths.UserRulesDirectory));

    [RelayCommand]
    private void OpenData() => _services.Shell.OpenInExplorer(AppPaths.EnsureDirectory(AppPaths.DataDirectory));
}
