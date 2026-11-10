using System.Text.Json;
using Headroom.Core.Optimization;
using Headroom.Core.Settings;
using Headroom.Core.SystemTools;
using Microsoft.Win32;

namespace Headroom.Core.RegistryCleanup;

public enum RegistryIssueKind
{
    OrphanUninstallEntry,
    MuiCache,
    FileAssociation,
    ProgramLeftover,
}

/// <summary>Item de registro comprovadamente órfão.</summary>
public sealed class RegistryIssue
{
    public required RegistryIssueKind Kind { get; init; }

    /// <summary>Chave completa (ex.: HKEY_CURRENT_USER\Software\...).</summary>
    public required string KeyPath { get; init; }

    /// <summary>Valor a apagar; null = apagar a chave inteira.</summary>
    public string? ValueName { get; init; }

    public required string Title { get; init; }
    public required string Reason { get; init; }
    public bool IsSelected { get; set; } = true;

    public string KindText => Kind switch
    {
        RegistryIssueKind.OrphanUninstallEntry => "Entrada de desinstalação órfã",
        RegistryIssueKind.MuiCache => "MUICache de programa inexistente",
        RegistryIssueKind.ProgramLeftover => "Rastro de programa desinstalado",
        _ => "Associação de arquivo para programa inexistente",
    };
}

/// <summary>Backup de uma correção (pasta com arquivos .reg e manifesto), usado pelo "Desfazer".</summary>
public sealed record RegistryBackup(string Folder, DateTime Date, int Count, bool RequiresAdmin);

/// <summary>
/// Limpeza de registro conservadora: só itens comprovadamente órfãos (o executável/pasta referenciado não
/// existe em um disco conectado). Antes de apagar, cada chave é exportada para .reg; "Desfazer" reimporta.
/// O ganho de espaço/desempenho é mínimo — existe para organização.
/// </summary>
public static class RegistryCleaner
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string MuiCache = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";

    public static List<RegistryIssue> Scan()
    {
        var issues = new List<RegistryIssue>();
        ScanUninstall(issues, RegistryHive.CurrentUser, RegistryView.Registry64, "HKEY_CURRENT_USER", Uninstall);
        ScanUninstall(issues, RegistryHive.LocalMachine, RegistryView.Registry64, "HKEY_LOCAL_MACHINE", Uninstall);
        ScanUninstall(issues, RegistryHive.LocalMachine, RegistryView.Registry64, "HKEY_LOCAL_MACHINE", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        ScanMuiCache(issues);
        ScanFileAssociations(issues);
        return issues;
    }

    /// <summary>true se o caminho é absoluto, está em disco fixo conectado e não existe.</summary>
    internal static bool IsMissingLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
            if (drive is null || !drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                return false; // disco removível/desconectado não prova que o programa sumiu
            }

            return !File.Exists(path) && !Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return false;
        }
    }

    private static void ScanUninstall(List<RegistryIssue> issues, RegistryHive hive, RegistryView view, string hiveName, string path)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(path);
            foreach (var name in uninstall?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var key = uninstall!.OpenSubKey(name);
                string? display = key?.GetValue("DisplayName") as string;
                string? command = key?.GetValue("UninstallString") as string;
                if (key is null || string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(command) ||
                    command.Contains("msiexec", StringComparison.OrdinalIgnoreCase) || command.Contains("rundll32", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? exe = StartupManager.ExtractExecutable(command);
                string? location = (key.GetValue("InstallLocation") as string)?.Trim('"');
                bool exeMissing = IsMissingLocalPath(exe);
                bool locationOk = !string.IsNullOrWhiteSpace(location) && Directory.Exists(location);
                if (exeMissing && !locationOk)
                {
                    issues.Add(new RegistryIssue
                    {
                        Kind = RegistryIssueKind.OrphanUninstallEntry,
                        KeyPath = $@"{hiveName}\{path}\{name}",
                        Title = display,
                        Reason = $"O desinstalador não existe mais: {exe}",
                    });
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void ScanMuiCache(List<RegistryIssue> issues)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MuiCache);
            foreach (var value in key?.GetValueNames() ?? Array.Empty<string>())
            {
                // "C:\app\x.exe.FriendlyAppName" ou ".ApplicationCompany"
                int idx = value.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    continue;
                }

                string exe = value[..(idx + 4)];
                if (IsMissingLocalPath(exe))
                {
                    issues.Add(new RegistryIssue
                    {
                        Kind = RegistryIssueKind.MuiCache,
                        KeyPath = $@"HKEY_CURRENT_USER\{MuiCache}",
                        ValueName = value,
                        Title = Path.GetFileName(exe),
                        Reason = $"Programa não existe mais: {exe}",
                    });
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void ScanFileAssociations(List<RegistryIssue> issues)
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes");
            if (classes is null)
            {
                return;
            }

            foreach (var progId in classes.GetSubKeyNames())
            {
                if (progId.StartsWith('.') || progId.Equals("Local Settings", StringComparison.OrdinalIgnoreCase) ||
                    progId.Equals("Applications", StringComparison.OrdinalIgnoreCase) || progId.Equals("CLSID", StringComparison.OrdinalIgnoreCase) ||
                    progId.Equals("Interface", StringComparison.OrdinalIgnoreCase) || progId.Equals("TypeLib", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CheckCommand(issues, classes, progId, $@"HKEY_CURRENT_USER\Software\Classes\{progId}");
            }

            using var apps = classes.OpenSubKey("Applications");
            foreach (var app in apps?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                CheckCommand(issues, apps!, app, $@"HKEY_CURRENT_USER\Software\Classes\Applications\{app}");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void CheckCommand(List<RegistryIssue> issues, RegistryKey parent, string name, string fullPath)
    {
        using var command = parent.OpenSubKey($@"{name}\shell\open\command");
        if (command?.GetValue(null) is not string line)
        {
            return;
        }

        string? exe = StartupManager.ExtractExecutable(line);
        if (exe is not null && !exe.Contains('%') && IsMissingLocalPath(exe))
        {
            issues.Add(new RegistryIssue
            {
                Kind = RegistryIssueKind.FileAssociation,
                KeyPath = fullPath,
                Title = name,
                Reason = $"\"Abrir com\" aponta para um programa que não existe: {exe}",
            });
        }
    }

    /// <summary>Exporta (reg export) cada chave afetada e só então apaga. Retorna a pasta de backup.</summary>
    public static async Task<(string BackupFolder, int Fixed, List<string> Errors)> FixAsync(IReadOnlyList<RegistryIssue> issues,
        CancellationToken ct)
    {
        string folder = AppPaths.EnsureDirectory(Path.Combine(AppPaths.RegistryBackupsDirectory, $"registro-{DateTime.Now:yyyyMMdd-HHmmss}"));
        var errors = new List<string>();
        var exported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var key in issues.Select(i => i.KeyPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string file = Path.Combine(folder, $"{++index:D4}.reg");
            var run = await CommandRunner.RunAsync(CommandRunner.SystemTool("reg.exe"), $"export \"{key}\" \"{file}\" /y",
                TimeSpan.FromMinutes(1), cancellationToken: ct).ConfigureAwait(false);
            if (run.Success && File.Exists(file))
            {
                exported[key] = file;
            }
            else
            {
                errors.Add($"Backup falhou para {key}: {run.Combined.Trim()}");
            }
        }

        int fixedCount = 0;
        foreach (var issue in issues)
        {
            if (!exported.ContainsKey(issue.KeyPath))
            {
                continue; // nunca apaga sem backup
            }

            try
            {
                var (hive, sub) = Split(issue.KeyPath);
                if (issue.ValueName is null)
                {
                    hive.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                }
                else
                {
                    using var key = hive.OpenSubKey(sub, writable: true);
                    key?.DeleteValue(issue.ValueName, throwOnMissingValue: false);
                }

                fixedCount++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
            {
                errors.Add($"{issue.Title}: {ex.Message}");
            }
        }

        var manifest = new
        {
            Date = DateTime.Now,
            Issues = issues.Select(i => new { i.Kind, i.KeyPath, i.ValueName, i.Title, i.Reason }),
            Files = exported.Values.Select(Path.GetFileName),
            RequiresAdmin = issues.Any(i => i.KeyPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)),
        };
        await File.WriteAllTextAsync(Path.Combine(folder, "manifesto.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        return (folder, fixedCount, errors);
    }

    public static List<RegistryBackup> ListBackups()
    {
        var result = new List<RegistryBackup>();
        if (!Directory.Exists(AppPaths.RegistryBackupsDirectory))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(AppPaths.RegistryBackupsDirectory))
        {
            var files = Directory.GetFiles(dir, "*.reg");
            if (files.Length == 0)
            {
                continue;
            }

            bool admin = files.Any(f => File.ReadAllText(f).Contains("[HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase));
            result.Add(new RegistryBackup(dir, Directory.GetCreationTime(dir), files.Length, admin));
        }

        return result.OrderByDescending(b => b.Date).ToList();
    }

    /// <summary>Desfaz uma correção reimportando os .reg do backup.</summary>
    public static async Task<(int Restored, List<string> Errors)> UndoAsync(RegistryBackup backup, CancellationToken ct)
    {
        int restored = 0;
        var errors = new List<string>();
        foreach (var file in Directory.GetFiles(backup.Folder, "*.reg").OrderBy(f => f))
        {
            var run = await CommandRunner.RunAsync(CommandRunner.SystemTool("reg.exe"), $"import \"{file}\"", TimeSpan.FromMinutes(1),
                cancellationToken: ct).ConfigureAwait(false);
            if (run.Success)
            {
                restored++;
            }
            else
            {
                errors.Add($"{Path.GetFileName(file)}: {run.Combined.Trim()}");
            }
        }

        return (restored, errors);
    }

    private static (RegistryKey Hive, string SubKey) Split(string fullPath)
    {
        // Sempre na visão de 64 bits: caminhos com WOW6432Node já apontam explicitamente para a área de 32 bits.
        int slash = fullPath.IndexOf('\\');
        string hive = fullPath[..slash];
        string sub = fullPath[(slash + 1)..];
        return hive.ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" => (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64), sub),
            "HKEY_LOCAL_MACHINE" => (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), sub),
            _ => throw new ArgumentException("Hive não suportado: " + hive),
        };
    }
}
