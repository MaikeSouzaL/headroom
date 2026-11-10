using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Services;
using Headroom.Core.RegistryCleanup;

namespace Headroom.App.ViewModels;

/// <summary>Item de registro órfão com marcação.</summary>
public sealed partial class RegistryIssueViewModel : ObservableObject
{
    public RegistryIssueViewModel(RegistryIssue issue)
    {
        Issue = issue;
        _isSelected = issue.IsSelected;
    }

    public RegistryIssue Issue { get; }
    public string Title => Issue.Title;
    public string Kind => Issue.KindText;
    public string Reason => Issue.Reason;
    public string Location => Issue.ValueName is null ? Issue.KeyPath : $"{Issue.KeyPath} → {Issue.ValueName}";
    public bool IsMachineWide => Issue.KeyPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => Issue.IsSelected = value;
}

/// <summary>Registro (Avançado): só itens comprovadamente órfãos, backup .reg obrigatório e Desfazer.</summary>
public sealed partial class RegistryViewModel : ObservableObject
{
    private readonly AppServices _services;

    public RegistryViewModel(AppServices services)
    {
        _services = services;
        LoadBackups();
    }

    public ObservableCollection<RegistryIssueViewModel> Issues { get; } = new();
    public ObservableCollection<RegistryBackup> Backups { get; } = new();
    public bool IsAdministrator => _services.IsAdministrator;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _statusText = "Ative a limpeza de registro abaixo para procurar itens órfãos.";
    [ObservableProperty] private RegistryBackup? _selectedBackup;

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            var issues = await Task.Run(RegistryCleaner.Scan);
            Issues.Clear();
            foreach (var issue in issues)
            {
                // Itens de HKLM exigem administrador para corrigir: só vêm marcados quando há permissão.
                issue.IsSelected = IsAdministrator || !issue.KeyPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase);
                Issues.Add(new RegistryIssueViewModel(issue));
            }

            StatusText = issues.Count == 0
                ? "Nenhum item órfão encontrado. Seu registro está organizado."
                : $"{issues.Count} item(ns) órfão(s) encontrados. Lembre-se: o ganho de espaço e desempenho é mínimo.";
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Falha ao verificar o registro", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task FixAsync()
    {
        var selected = Issues.Where(i => i.IsSelected).Select(i => i.Issue).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (!IsAdministrator && selected.Any(i => i.KeyPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)))
        {
            await _services.Dialogs.InfoAsync("Requer administrador", "Alguns itens são do computador (HKLM) e exigem administrador. Desmarque-os ou reinicie como administrador.");
            return;
        }

        if (!await _services.Dialogs.ConfirmAsync("Corrigir registro (Avançado)",
                $"Apagar {selected.Count} item(ns) órfão(s) do registro?\n\nAntes, cada chave é exportada para um arquivo .reg; " +
                "você pode desfazer depois em \"Backups\".", "Fazer backup e corrigir", danger: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (folder, fixedCount, errors) = await RegistryCleaner.FixAsync(selected, CancellationToken.None);
            foreach (var row in Issues.Where(i => i.IsSelected).ToList())
            {
                Issues.Remove(row);
            }

            StatusText = $"{fixedCount} item(ns) corrigido(s). Backup em: {folder}" +
                         (errors.Count > 0 ? $"\nAvisos: {string.Join("; ", errors)}" : string.Empty);
            LoadBackups();
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Falha ao corrigir o registro", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (SelectedBackup is not { } backup)
        {
            await _services.Dialogs.InfoAsync("Desfazer", "Escolha um backup na lista.");
            return;
        }

        if (backup.RequiresAdmin && !IsAdministrator)
        {
            await _services.Dialogs.InfoAsync("Requer administrador", "Este backup contém chaves do computador (HKLM); restaurar exige administrador.");
            return;
        }

        if (!await _services.Dialogs.ConfirmAsync("Desfazer correção", $"Reimportar {backup.Count} arquivo(s) .reg de {backup.Date:dd/MM/yyyy HH:mm}?", "Restaurar"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (restored, errors) = await RegistryCleaner.UndoAsync(backup, CancellationToken.None);
            StatusText = $"{restored} arquivo(s) restaurado(s)." + (errors.Count > 0 ? " Erros: " + string.Join("; ", errors) : string.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        if (SelectedBackup is { } backup)
        {
            _services.Shell.OpenInExplorer(backup.Folder);
        }
    }

    private void LoadBackups()
    {
        Backups.Clear();
        foreach (var backup in RegistryCleaner.ListBackups())
        {
            Backups.Add(backup);
        }
    }
}
