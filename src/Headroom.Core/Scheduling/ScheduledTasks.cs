using Headroom.Core.Settings;
using Headroom.Core.SystemTools;

namespace Headroom.Core.Scheduling;

/// <summary>Tarefas do Agendador de Tarefas do Windows criadas pelo Headroom (schtasks.exe).</summary>
public static class ScheduledTasks
{
    public const string CleanupTaskName = @"Headroom\Limpeza agendada";
    public const string SpaceAlertTaskName = @"Headroom\Alerta de espaço livre";

    /// <summary>Monta os argumentos do schtasks para a limpeza agendada (testável).</summary>
    public static string BuildCleanupArguments(string executable, ScheduleFrequency frequency, string time, string profileId, bool highestPrivileges)
    {
        if (!TimeOnly.TryParse(time, out var parsed))
        {
            throw new ArgumentException("Horário inválido. Use HH:mm, por exemplo 10:00.", nameof(time));
        }

        string schedule = frequency switch
        {
            ScheduleFrequency.Daily => "/SC DAILY",
            ScheduleFrequency.Weekly => "/SC WEEKLY /D MON",
            _ => "/SC MONTHLY /D 1",
        };
        string command = $"\\\"{executable}\\\" --profile {profileId} --silent --log --notify";
        return $"/Create /TN \"{CleanupTaskName}\" /TR \"{command}\" {schedule} /ST {parsed:HH\\:mm} /F" +
               (highestPrivileges ? " /RL HIGHEST" : string.Empty);
    }

    public static Task<CommandResult> CreateCleanupAsync(ScheduleFrequency frequency, string time, string profileId, bool highestPrivileges,
        CancellationToken ct = default) =>
        CommandRunner.RunAsync(CommandRunner.SystemTool("schtasks.exe"),
            BuildCleanupArguments(AppPaths.ExecutablePath, frequency, time, profileId, highestPrivileges), TimeSpan.FromMinutes(1),
            cancellationToken: ct);

    /// <summary>Verifica o espaço livre a cada 4 horas e notifica se algum disco ficar abaixo do limite.</summary>
    public static Task<CommandResult> CreateSpaceAlertAsync(CancellationToken ct = default)
    {
        string command = $"\\\"{AppPaths.ExecutablePath}\\\" --check-space --notify";
        return CommandRunner.RunAsync(CommandRunner.SystemTool("schtasks.exe"),
            $"/Create /TN \"{SpaceAlertTaskName}\" /TR \"{command}\" /SC HOURLY /MO 4 /F", TimeSpan.FromMinutes(1), cancellationToken: ct);
    }

    public static Task<CommandResult> DeleteAsync(string taskName, CancellationToken ct = default) =>
        CommandRunner.RunAsync(CommandRunner.SystemTool("schtasks.exe"), $"/Delete /TN \"{taskName}\" /F", TimeSpan.FromMinutes(1),
            cancellationToken: ct);

    public static async Task<bool> ExistsAsync(string taskName, CancellationToken ct = default)
    {
        var run = await CommandRunner.RunAsync(CommandRunner.SystemTool("schtasks.exe"), $"/Query /TN \"{taskName}\"", TimeSpan.FromSeconds(30),
            cancellationToken: ct).ConfigureAwait(false);
        return run.ExitCode == 0;
    }
}
