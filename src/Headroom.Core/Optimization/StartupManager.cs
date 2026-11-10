using System.Diagnostics;
using System.Text.Json;
using Headroom.Core.SystemTools;
using Microsoft.Win32;

namespace Headroom.Core.Optimization;

public enum StartupSource
{
    RegistryUser,
    RegistryMachine,
    RegistryMachine32,
    StartupFolderUser,
    StartupFolderCommon,
    ScheduledTask,
}

/// <summary>Um programa que inicia junto com o Windows.</summary>
public sealed class StartupItem
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required StartupSource Source { get; init; }
    public bool Enabled { get; set; }
    public string? ExecutablePath { get; init; }
    public string? Publisher { get; init; }
    public string? TaskPath { get; init; }

    /// <summary>Itens do computador (HKLM, pasta comum, tarefas) exigem administrador para alterar.</summary>
    public bool RequiresAdmin => Source is StartupSource.RegistryMachine or StartupSource.RegistryMachine32
        or StartupSource.StartupFolderCommon or StartupSource.ScheduledTask;

    public string SourceText => Source switch
    {
        StartupSource.RegistryUser => @"Registro (HKCU\...\Run)",
        StartupSource.RegistryMachine => @"Registro (HKLM\...\Run)",
        StartupSource.RegistryMachine32 => @"Registro 32 bits (HKLM\...\Run)",
        StartupSource.StartupFolderUser => "Pasta Inicializar do usuário",
        StartupSource.StartupFolderCommon => "Pasta Inicializar de todos os usuários",
        _ => $"Tarefa agendada ({TaskPath}{Name})",
    };

    public bool ExecutableExists => ExecutablePath is not null && File.Exists(ExecutablePath);
}

/// <summary>
/// Gerenciador de inicialização. Desativa itens do mesmo jeito que o Gerenciador de Tarefas do Windows
/// (chaves StartupApproved), sem apagar nada — é totalmente reversível.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static async Task<List<StartupItem>> ListAsync(bool includeScheduledTasks = true, CancellationToken ct = default)
    {
        var items = new List<StartupItem>();
        ReadRunKey(items, Registry.CurrentUser, RunKey, StartupSource.RegistryUser);
        ReadRunKey(items, Registry.LocalMachine, RunKey, StartupSource.RegistryMachine);
        ReadRunKey(items, Registry.LocalMachine, Run32Key, StartupSource.RegistryMachine32);
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.StartupFolderUser);
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.StartupFolderCommon);
        if (includeScheduledTasks)
        {
            items.AddRange(await ReadScheduledTasksAsync(ct).ConfigureAwait(false));
        }

        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ReadRunKey(List<StartupItem> items, RegistryKey hive, string path, StartupSource source)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
            {
                string command = key.GetValue(name)?.ToString() ?? string.Empty;
                string? exe = ExtractExecutable(command);
                items.Add(new StartupItem
                {
                    Name = name,
                    Command = command,
                    Source = source,
                    ExecutablePath = exe,
                    Publisher = Publisher(exe),
                    Enabled = IsApproved(source, name),
                });
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void ReadFolder(List<StartupItem> items, string folder, StartupSource source)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(folder).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileName(file);
                items.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = file,
                    Source = source,
                    ExecutablePath = file,
                    Publisher = Publisher(file),
                    Enabled = IsApproved(source, name),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<List<StartupItem>> ReadScheduledTasksAsync(CancellationToken ct)
    {
        const string script =
            "Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' -and " +
            "($_.Triggers | Where-Object { $_.CimClass.CimClassName -in 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger' }) } | " +
            "ForEach-Object { $a = $_.Actions | Select-Object -First 1; [pscustomobject]@{ Name=$_.TaskName; Path=$_.TaskPath; " +
            "State=\"$($_.State)\"; Exec=$a.Execute; Args=$a.Arguments } } | ConvertTo-Json -Compress";
        var items = new List<StartupItem>();
        var run = await CommandRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(1), cancellationToken: ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(run.Output))
        {
            return items;
        }

        try
        {
            using var doc = JsonDocument.Parse(run.Output.Trim());
            foreach (var t in Cleanup.Rules.JsonHelpers.Items(doc.RootElement))
            {
                string exec = Environment.ExpandEnvironmentVariables((Cleanup.Rules.JsonHelpers.Str(t, "Exec") ?? string.Empty).Trim('"'));
                items.Add(new StartupItem
                {
                    Name = Cleanup.Rules.JsonHelpers.Str(t, "Name") ?? "?",
                    TaskPath = Cleanup.Rules.JsonHelpers.Str(t, "Path") ?? "\\",
                    Command = (exec + " " + Cleanup.Rules.JsonHelpers.Str(t, "Args")).Trim(),
                    Source = StartupSource.ScheduledTask,
                    ExecutablePath = exec.Length > 0 ? exec : null,
                    Publisher = Publisher(exec),
                    Enabled = !string.Equals(Cleanup.Rules.JsonHelpers.Str(t, "State"), "Disabled", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch (JsonException)
        {
        }

        return items;
    }

    /// <summary>Ativa/desativa um item (reversível). Tarefas usam Enable/Disable-ScheduledTask.</summary>
    public static async Task SetEnabledAsync(StartupItem item, bool enabled, CancellationToken ct = default)
    {
        if (item.Source == StartupSource.ScheduledTask)
        {
            string cmd = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
            string script = $"{cmd} -TaskName '{item.Name.Replace("'", "''")}' -TaskPath '{(item.TaskPath ?? "\\").Replace("'", "''")}' -ErrorAction Stop | Out-Null; 'OK'";
            var run = await CommandRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(1), cancellationToken: ct).ConfigureAwait(false);
            if (!run.Output.Contains("OK", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Não foi possível alterar a tarefa agendada: " + run.Error.Trim());
            }

            item.Enabled = enabled;
            return;
        }

        var (hive, subKey) = ApprovedLocation(item.Source);
        string valueName = item.Source is StartupSource.StartupFolderUser or StartupSource.StartupFolderCommon
            ? Path.GetFileName(item.Command)
            : item.Name;

        using var key = hive.CreateSubKey(subKey, writable: true)
                        ?? throw new InvalidOperationException("Não foi possível abrir a chave de inicialização.");
        var data = new byte[12];
        if (enabled)
        {
            data[0] = 0x02;
        }
        else
        {
            data[0] = 0x03;
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        }

        key.SetValue(valueName, data, RegistryValueKind.Binary);
        item.Enabled = enabled;
    }

    private static bool IsApproved(StartupSource source, string valueName)
    {
        try
        {
            var (hive, subKey) = ApprovedLocation(source);
            using var key = hive.OpenSubKey(subKey);
            if (key?.GetValue(valueName) is byte[] { Length: > 0 } data)
            {
                return (data[0] & 1) == 0; // 0x02/0x06 = ativado; 0x03/0x01 = desativado
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return true;
    }

    private static (RegistryKey Hive, string SubKey) ApprovedLocation(StartupSource source) => source switch
    {
        StartupSource.RegistryUser => (Registry.CurrentUser, ApprovedRoot + @"\Run"),
        StartupSource.RegistryMachine => (Registry.LocalMachine, ApprovedRoot + @"\Run"),
        StartupSource.RegistryMachine32 => (Registry.LocalMachine, ApprovedRoot + @"\Run32"),
        StartupSource.StartupFolderUser => (Registry.CurrentUser, ApprovedRoot + @"\StartupFolder"),
        StartupSource.StartupFolderCommon => (Registry.LocalMachine, ApprovedRoot + @"\StartupFolder"),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>Extrai o executável de uma linha de comando ("C:\x\a.exe" --arg ou C:\x\a.exe /b).</summary>
    public static string? ExtractExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }

        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            return c[..(exe + 4)];
        }

        int space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }

    private static string? Publisher(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path) || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string? company = FileVersionInfo.GetVersionInfo(path).CompanyName;
            return string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
