using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cleanup.Logging;
using Headroom.Core.Formatting;
using Headroom.Core.RegistryCleanup;
using Headroom.Core.Settings;
using Headroom.Core.SystemTools;

namespace Headroom.Core.Programs;

/// <summary>Resultado da remoção dos rastros.</summary>
public sealed class LeftoverRemovalResult
{
    public int Removed { get; internal set; }
    public long BytesRemoved { get; internal set; }
    public bool MovedToRecycleBin { get; internal init; }
    public List<string> Skipped { get; } = new();
    public List<string> Errors { get; } = new();
    public string? LogPath { get; internal init; }

    /// <summary>Pasta com os backups (.reg do registro e dos serviços, .xml das tarefas).</summary>
    public string? BackupFolder { get; internal set; }

    public string Summary =>
        $"{Removed} rastro(s) removido(s)" +
        (BytesRemoved > 0 ? $", {ByteSize.Format(BytesRemoved)}{(MovedToRecycleBin ? " na Lixeira" : string.Empty)}" : string.Empty) +
        (Skipped.Count > 0 ? $"; {Skipped.Count} pulado(s)" : string.Empty) +
        (Errors.Count > 0 ? $"; {Errors.Count} erro(s)" : string.Empty) + ".";
}

/// <summary>
/// Remove os rastros escolhidos com o máximo de segurança: arquivos e pastas vão para a Lixeira (lista negra e
/// arquivos em uso respeitados), o registro é exportado para .reg antes de apagar (desfazível na tela Registro),
/// tarefas agendadas têm o XML copiado antes de sair e serviços têm a chave exportada antes do "sc delete".
/// </summary>
public static class ProgramLeftoverRemover
{
    public static async Task<LeftoverRemovalResult> RemoveAsync(
        IReadOnlyList<ProgramLeftover> leftovers,
        string programName,
        bool isAdministrator,
        DeletionMode mode = DeletionMode.RecycleBin,
        IRecycler? recycler = null,
        CancellationToken cancellationToken = default)
    {
        using var log = CleanupLog.Create("desinstalacao");
        log.Section($"Rastros de {programName}");
        var result = new LeftoverRemovalResult { LogPath = log.FilePath, MovedToRecycleBin = mode == DeletionMode.RecycleBin };

        var work = new List<ProgramLeftover>();
        foreach (var item in leftovers)
        {
            if (item.RequiresAdmin && !isAdministrator && item.Kind is not (LeftoverKind.Folder or LeftoverKind.Shortcut))
            {
                result.Skipped.Add($"{item.Display}: requer administrador");
                log.Info($"Pulado (requer administrador): {item.Display}");
            }
            else
            {
                work.Add(item);
            }
        }

        string backup = AppPaths.EnsureDirectory(Path.Combine(AppPaths.RegistryBackupsDirectory, $"desinstalacao-{DateTime.Now:yyyyMMdd-HHmmss}"));
        result.BackupFolder = backup;

        await RemoveServicesAsync(work.Where(i => i.Kind == LeftoverKind.Service).ToList(), backup, result, log, cancellationToken).ConfigureAwait(false);
        await RemoveTasksAsync(work.Where(i => i.Kind == LeftoverKind.ScheduledTask).ToList(), backup, result, log, cancellationToken).ConfigureAwait(false);
        await RemoveRegistryAsync(work.Where(i => i.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue).ToList(), result, log, cancellationToken)
            .ConfigureAwait(false);
        await RemoveFilesAsync(work.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.Shortcut).ToList(), mode, recycler, result, log, cancellationToken)
            .ConfigureAwait(false);

        if (!Directory.EnumerateFileSystemEntries(backup).Any())
        {
            Directory.Delete(backup);
            result.BackupFolder = null;
        }

        log.Info(result.Summary);
        return result;
    }

    private static async Task RemoveServicesAsync(List<ProgramLeftover> services, string backup, LeftoverRemovalResult result, CleanupLog log,
        CancellationToken ct)
    {
        foreach (var service in services)
        {
            string key = $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{service.Path}";
            string file = Path.Combine(backup, $"servico-{Sanitize(service.Path)}.reg");
            var export = await CommandRunner.RunAsync(CommandRunner.SystemTool("reg.exe"), $"export \"{key}\" \"{file}\" /y",
                TimeSpan.FromMinutes(1), cancellationToken: ct).ConfigureAwait(false);
            if (!export.Success)
            {
                result.Errors.Add($"Serviço {service.Path}: não foi possível fazer o backup, nada foi alterado.");
                continue;
            }

            await CommandRunner.RunAsync(CommandRunner.SystemTool("sc.exe"), $"stop \"{service.Path}\"", TimeSpan.FromSeconds(30),
                cancellationToken: ct).ConfigureAwait(false);
            var delete = await CommandRunner.RunAsync(CommandRunner.SystemTool("sc.exe"), $"delete \"{service.Path}\"", TimeSpan.FromSeconds(30),
                cancellationToken: ct).ConfigureAwait(false);
            if (delete.Success)
            {
                result.Removed++;
                log.Item("Serviço removido", 0, service.Path);
            }
            else
            {
                result.Errors.Add($"Serviço {service.Path}: {delete.Combined.Trim()}");
            }
        }
    }

    private static async Task RemoveTasksAsync(List<ProgramLeftover> tasks, string backup, LeftoverRemovalResult result, CleanupLog log,
        CancellationToken ct)
    {
        string tasksRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
        foreach (var task in tasks)
        {
            try
            {
                string source = tasksRoot + task.Path;
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(backup, $"tarefa-{Sanitize(task.Path)}.xml"), overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"Tarefa {task.Path}: não foi possível fazer o backup, nada foi alterado.");
                continue;
            }

            var delete = await CommandRunner.RunAsync(CommandRunner.SystemTool("schtasks.exe"), $"/delete /tn \"{task.Path}\" /f",
                TimeSpan.FromSeconds(30), cancellationToken: ct).ConfigureAwait(false);
            if (delete.Success)
            {
                result.Removed++;
                log.Item("Tarefa agendada removida", 0, task.Path);
            }
            else
            {
                result.Errors.Add($"Tarefa {task.Path}: {delete.Combined.Trim()}");
            }
        }
    }

    private static async Task RemoveRegistryAsync(List<ProgramLeftover> entries, LeftoverRemovalResult result, CleanupLog log, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var issues = entries.Select(e => new RegistryIssue
        {
            Kind = RegistryIssueKind.ProgramLeftover,
            KeyPath = e.Path,
            ValueName = e.ValueName,
            Title = e.Display,
            Reason = e.Reason,
        }).ToList();

        // Exporta cada chave para .reg e só então apaga (nunca apaga sem backup).
        var (folder, fixedCount, errors) = await RegistryCleaner.FixAsync(issues, ct).ConfigureAwait(false);
        result.Removed += fixedCount;
        result.Errors.AddRange(errors);
        foreach (var entry in entries)
        {
            log.Item("Registro removido", 0, entry.Display);
        }

        log.Info($"Backup do registro: {folder}");
    }

    private static async Task RemoveFilesAsync(List<ProgramLeftover> items, DeletionMode mode, IRecycler? recycler, LeftoverRemovalResult result,
        CleanupLog log, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        var deleter = new FileDeleter(recycler: recycler, log: log);
        var targets = items.Select(i => new DeletionTarget(i.Path, i.Kind == LeftoverKind.Folder, i.Size)).ToList();
        var deletion = await deleter.DeleteAsync(targets, new DeletionOptions { Mode = mode, LogLabel = "Arquivos e pastas" },
            cancellationToken: ct).ConfigureAwait(false);

        result.BytesRemoved += deletion.BytesDeleted;
        foreach (var item in items)
        {
            bool gone = item.Kind == LeftoverKind.Folder ? !Directory.Exists(item.Path) : !File.Exists(item.Path);
            if (gone)
            {
                result.Removed++;
            }
            else
            {
                result.Skipped.Add($"{item.Path}: em uso, sem permissão ou protegido (veja o log)");
            }
        }

        if (deletion.Errors > 0)
        {
            result.Errors.Add($"{deletion.Errors} erro(s) ao apagar arquivos (veja o log).");
        }
    }

    private static string Sanitize(string text) =>
        string.Concat(text.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == '\\' ? '_' : ch)).Trim('_');
}
