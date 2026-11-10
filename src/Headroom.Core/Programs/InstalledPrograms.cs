using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Headroom.Core.SystemTools;
using Microsoft.Win32;

namespace Headroom.Core.Programs;

/// <summary>Programa instalado (chaves Uninstall do registro).</summary>
public sealed class InstalledProgram
{
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? DisplayIcon { get; init; }
    public long EstimatedSizeBytes { get; init; }
    public DateTime? InstallDate { get; init; }
    public required string RegistryKey { get; init; }
    public bool IsUserInstall { get; init; }
    public bool Is32Bit { get; init; }

    /// <summary>Tamanho real medido na pasta de instalação (preenchido sob demanda).</summary>
    public long? MeasuredSizeBytes { get; set; }

    /// <summary>Último uso estimado (Prefetch ou data de acesso dos executáveis).</summary>
    public DateTime? LastUsed { get; set; }

    public long SizeBytes => MeasuredSizeBytes ?? EstimatedSizeBytes;

    /// <summary>Pasta mais provável do programa (InstallLocation, ou pasta do ícone/desinstalador).</summary>
    public string? ProbableFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(InstallLocation) && Directory.Exists(InstallLocation.Trim('"')))
            {
                return InstallLocation.Trim('"').TrimEnd('\\');
            }

            foreach (var candidate in new[] { DisplayIcon, UninstallString })
            {
                string? exe = Optimization.StartupManager.ExtractExecutable(candidate?.Split(',')[0] ?? string.Empty);
                if (exe is not null && Path.IsPathFullyQualified(exe) && !IsWindowsTool(exe))
                {
                    string? dir = Path.GetDirectoryName(exe);
                    if (dir is not null && Directory.Exists(dir) && !IsSharedFolder(dir))
                    {
                        return dir;
                    }
                }
            }

            return null;
        }
    }

    private static bool IsWindowsTool(string exe) =>
        exe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);

    private static bool IsSharedFolder(string dir)
    {
        string name = Path.GetFileName(dir);
        return name.Equals("Package Cache", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) ||
               dir.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Lê, mede e desinstala programas.</summary>
public static partial class InstalledProgramsService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> List()
    {
        var programs = new List<InstalledProgram>();
        Read(programs, RegistryHive.LocalMachine, RegistryView.Registry64, user: false, is32: false);
        Read(programs, RegistryHive.LocalMachine, RegistryView.Registry32, user: false, is32: true);
        Read(programs, RegistryHive.CurrentUser, RegistryView.Registry64, user: true, is32: false);

        // O mesmo programa pode aparecer em mais de uma chave: mantém uma entrada por nome+versão.
        return programs
            .GroupBy(p => (p.Name.ToLowerInvariant(), (p.Version ?? string.Empty).ToLowerInvariant()))
            .Select(g => g.OrderByDescending(p => p.EstimatedSizeBytes).First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void Read(List<InstalledProgram> programs, RegistryHive hive, RegistryView view, bool user, bool is32)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);
            if (uninstall is null)
            {
                return;
            }

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(subName);
                if (key is null)
                {
                    continue;
                }

                string? name = key.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name) ||
                    Convert.ToInt32(key.GetValue("SystemComponent") ?? 0, CultureInfo.InvariantCulture) == 1 ||
                    key.GetValue("ParentKeyName") is not null ||
                    (key.GetValue("ReleaseType") as string) is "Update" or "Hotfix" or "Security Update")
                {
                    continue;
                }

                long sizeKb = key.GetValue("EstimatedSize") is int kb ? kb : 0;
                programs.Add(new InstalledProgram
                {
                    Name = name.Trim(),
                    Publisher = (key.GetValue("Publisher") as string)?.Trim(),
                    Version = key.GetValue("DisplayVersion") as string,
                    InstallLocation = (key.GetValue("InstallLocation") as string)?.Trim(),
                    UninstallString = key.GetValue("UninstallString") as string,
                    QuietUninstallString = key.GetValue("QuietUninstallString") as string,
                    DisplayIcon = key.GetValue("DisplayIcon") as string,
                    EstimatedSizeBytes = sizeKb * 1024,
                    InstallDate = ParseInstallDate(key.GetValue("InstallDate") as string),
                    RegistryKey = $@"{(user ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE")}\{(is32 && !user ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" : UninstallPath)}\{subName}",
                    IsUserInstall = user,
                    Is32Bit = is32,
                });
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or FormatException or InvalidCastException)
        {
        }
    }

    internal static DateTime? ParseInstallDate(string? value) =>
        value is { Length: 8 } && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Mede o tamanho real e estima o último uso de cada programa (pode demorar).</summary>
    public static void MeasureAll(IEnumerable<InstalledProgram> programs, bool isAdministrator, IProgress<InstalledProgram>? progress,
        CancellationToken ct)
    {
        var prefetch = isAdministrator ? ReadPrefetch() : new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(programs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, program =>
        {
            string? folder = program.ProbableFolder;
            if (folder is not null)
            {
                program.MeasuredSizeBytes = DiskMeasure.DirectorySize(folder, ct);
                program.LastUsed = EstimateLastUse(folder, prefetch);
            }

            progress?.Report(program);
        });
    }

    private static DateTime? EstimateLastUse(string folder, Dictionary<string, DateTime> prefetch)
    {
        DateTime? best = null;
        try
        {
            foreach (var exe in Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         MaxRecursionDepth = 2,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }).Take(50))
            {
                string name = Path.GetFileName(exe);
                if (name.Contains("unins", StringComparison.OrdinalIgnoreCase) || name.Contains("update", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (prefetch.TryGetValue(name, out var ran) && (best is null || ran > best))
                {
                    best = ran;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return best;
    }

    /// <summary>Prefetch (C:\Windows\Prefetch\APP.EXE-HASH.pf): a data do .pf é a última execução.</summary>
    private static Dictionary<string, DateTime> ReadPrefetch()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            foreach (var pf in new DirectoryInfo(dir).EnumerateFiles("*.pf"))
            {
                string exe = pf.Name[..Math.Max(0, pf.Name.LastIndexOf('-'))];
                if (exe.Length > 0 && (!map.TryGetValue(exe, out var existing) || pf.LastWriteTime > existing))
                {
                    map[exe] = pf.LastWriteTime;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return map;
    }

    /// <summary>Divide a linha de desinstalação em executável + argumentos (MsiExec /I vira /X).</summary>
    public static (string File, string Arguments) ParseUninstallString(string uninstallString)
    {
        string s = uninstallString.Trim();
        var msi = MsiRegex().Match(s);
        if (msi.Success)
        {
            return (CommandRunner.SystemTool("msiexec.exe"), $"/X{msi.Groups["guid"].Value}");
        }

        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end > 1)
            {
                return (s[1..end], s[(end + 1)..].Trim());
            }
        }

        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            return (s[..(exe + 4)], s[(exe + 4)..].Trim());
        }

        int space = s.IndexOf(' ');
        return space > 0 ? (s[..space], s[(space + 1)..]) : (s, string.Empty);
    }

    /// <summary>Inicia o desinstalador oficial do programa (o próprio desinstalador pede confirmação/elevação).</summary>
    public static Process? Uninstall(InstalledProgram program)
    {
        string? command = program.UninstallString ?? program.QuietUninstallString;
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Este programa não informa como ser desinstalado.");
        }

        var (file, arguments) = ParseUninstallString(Environment.ExpandEnvironmentVariables(command));
        return Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
    }

    /// <summary>Falso quando o desinstalador sumiu (a desinstalação normal é impossível; só resta remover os rastros).</summary>
    public static bool HasWorkingUninstaller(InstalledProgram program)
    {
        string? command = program.UninstallString ?? program.QuietUninstallString;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var (file, _) = ParseUninstallString(Environment.ExpandEnvironmentVariables(command));
        return !Path.IsPathFullyQualified(file) || File.Exists(file);
    }

    /// <summary>
    /// Verdadeiro enquanto há um desinstalador rodando nesta sessão. Muitos desinstaladores (Inno Setup, NSIS)
    /// se copiam para a pasta temporária e fecham o processo original logo no começo.
    /// </summary>
    public static bool IsAnyUninstallerRunning()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.SessionId != session)
                {
                    continue;
                }

                string name = process.ProcessName;
                if (name.StartsWith("_iu", StringComparison.OrdinalIgnoreCase) || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("uninst", StringComparison.OrdinalIgnoreCase) || name.Equals("Au_", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Un_A", StringComparison.OrdinalIgnoreCase) || name.Equals("Un", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [GeneratedRegex(@"msiexec(?:\.exe)?""?\s+/[IX]\s*(?<guid>\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiRegex();
}
