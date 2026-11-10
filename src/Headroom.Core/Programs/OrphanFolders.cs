using System.Diagnostics;
using System.Text.RegularExpressions;
using Headroom.Core.Cleanup;
using Headroom.Core.Cleanup.Safety;
using Microsoft.Win32;

namespace Headroom.Core.Programs;

/// <summary>
/// Restos de programas desinstalados: pastas em Program Files, ProgramData e AppData que nenhum
/// programa instalado, serviço ou processo em execução usa e que não são modificadas há 30 dias.
/// Cada item traz o motivo de ser considerado órfão. Nada vem marcado: é sempre "Revisar".
/// </summary>
public static partial class OrphanFolderDetector
{
    /// <summary>Pastas conhecidas do Windows/fabricantes que nunca são consideradas órfãs.</summary>
    private static readonly string[] KnownFolders =
    [
        "Microsoft*", "Windows*", "Common Files", "Internet Explorer", "ModifiableWindowsApps", "WindowsApps", "dotnet",
        "Reference Assemblies", "MSBuild", "Package Cache", "Packages", "Temp", "Uninstall Information", "PackageManagement",
        "Intel*", "NVIDIA*", "AMD", "ATI*", "Realtek", "Dell*", "HP", "Hewlett*", "Lenovo", "ASUS", "Acer", "MSI", "Synaptics",
        "Google", "Mozilla*", "Adobe", "Apple*", "Oracle", "Java", "IIS*", "MySQL", "PostgreSQL", "Git", "Docker", "VMware",
        "Comms", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps", "Downloaded Installations", "regid.*", "ssh", "USOPrivate",
        "USOShared", "SoftwareDistribution", "Package Cache", "Programs", "PeerDistRepub", "PlaceholderTileLogoFolder", "Publishers",
        "Comms", "IsolatedStorage", "History", "VirtualStore", "Sun", "Unity", "npm", "npm-cache", "pip", "uv", "pnpm", "Yarn",
        "JetBrains", "Code", "Steam", "Epic*", "Battle.net", "Ubisoft*", "EA*", "Origin", "GOG*", "Riot*", "ssl", "regid.1991-06.com.microsoft",
        "WinRAR", "7-Zip", "Spotify", "Discord", "Slack", "Zoom", "Opera*", "BraveSoftware", "Vivaldi", "Chromium", "Arc",
        "Desktop", "Documents", "Downloads", "Favorites", "Links", "Music", "Pictures", "Videos", "Saved Games", "Searches", "Contacts",
    ];

    public static async Task<List<CleanupItem>> FindAsync(IReadOnlyList<InstalledProgram> programs, CancellationToken ct)
    {
        var known = BuildKnownTokens(programs);
        var usedPaths = await Task.Run(() => CollectUsedPaths(programs), ct).ConfigureAwait(false);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        }.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase);

        var cutoff = DateTime.Now.AddDays(-30);
        var items = new List<CleanupItem>();
        await Task.Run(() =>
        {
            foreach (var root in roots)
            {
                foreach (var dir in SafeDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    string name = dir.Name;
                    if ((dir.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden)) != 0 ||
                        KnownFolders.Any(k => PathExpander.MatchesWildcard(name, k)) ||
                        DeletionGuard.Default.Check(dir.FullName).IsProtected)
                    {
                        continue;
                    }

                    string token = Normalize(name);
                    if (token.Length < 3 || known.Any(k => k.Contains(token, StringComparison.Ordinal) || (k.Length >= 4 && token.Contains(k, StringComparison.Ordinal))))
                    {
                        continue;
                    }

                    if (usedPaths.Any(p => ProtectedPaths.IsSameOrInside(p, dir.FullName)))
                    {
                        continue; // um serviço, processo ou programa aponta para esta pasta
                    }

                    var (size, files, newest) = Cleanup.Rules.PathRule.MeasureDirectory(dir.FullName, ct);
                    DateTime lastChange = newest == DateTime.MinValue ? dir.LastWriteTime : newest;
                    if (lastChange > cutoff || size == 0)
                    {
                        continue;
                    }

                    items.Add(new CleanupItem(dir.FullName, size, true, lastChange, files)
                    {
                        Reason = $"Nenhum programa instalado tem nome/fabricante parecido com \"{name}\"; nenhum serviço ou processo usa a pasta; " +
                                 $"sem modificação desde {lastChange:dd/MM/yyyy}.",
                        IsSelected = false,
                    });
                }
            }
        }, ct).ConfigureAwait(false);

        return items.OrderByDescending(i => i.Size).ToList();
    }

    private static List<string> BuildKnownTokens(IReadOnlyList<InstalledProgram> programs)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in programs)
        {
            foreach (var text in new[] { p.Name, p.Publisher })
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                string full = Normalize(text);
                if (full.Length >= 3)
                {
                    tokens.Add(full);
                }

                // Primeira palavra significativa ("Corsair iCUE" → "corsair").
                string first = Normalize(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty);
                if (first.Length >= 4)
                {
                    tokens.Add(first);
                }
            }

            foreach (var path in new[] { p.InstallLocation, p.ProbableFolder })
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    foreach (var segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1))
                    {
                        string t = Normalize(segment);
                        if (t.Length >= 3)
                        {
                            tokens.Add(t);
                        }
                    }
                }
            }
        }

        return tokens.ToList();
    }

    private static List<string> CollectUsedPaths(IReadOnlyList<InstalledProgram> programs)
    {
        var paths = new List<string>();
        foreach (var p in programs)
        {
            if (p.ProbableFolder is { } f)
            {
                paths.Add(f);
            }
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainModule?.FileName is { } file)
                {
                    paths.Add(file);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var name in services?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var key = services!.OpenSubKey(name);
                if (key?.GetValue("ImagePath") is string image &&
                    Optimization.StartupManager.ExtractExecutable(image) is { } exe && Path.IsPathFullyQualified(exe))
                {
                    paths.Add(exe);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return paths;
    }

    private static IEnumerable<DirectoryInfo> SafeDirectories(string root)
    {
        try
        {
            return new DirectoryInfo(root).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<DirectoryInfo>();
        }
    }

    /// <summary>Minúsculas, só letras e números ("Corsair iCUE 4" → "corsairicue4").</summary>
    internal static string Normalize(string text) => NonAlnum().Replace(text.ToLowerInvariant(), string.Empty);

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();
}
