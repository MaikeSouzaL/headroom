using System.Text.Json;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.SystemTools;

namespace Headroom.Core.Programs;

/// <summary>App da Microsoft Store (pacote Appx/MSIX) do usuário atual.</summary>
public sealed class StoreApp
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public string? Version { get; init; }
    public string? InstallLocation { get; init; }
    public bool NonRemovable { get; init; }
    public bool IsBloatware { get; init; }
    public long SizeBytes { get; set; }

    /// <summary>Nome amigável a partir do nome do pacote (ex.: Microsoft.BingWeather → Bing Weather).</summary>
    public string DisplayName
    {
        get
        {
            string n = Name.Contains('.') ? Name[(Name.IndexOf('.') + 1)..] : Name;
            return System.Text.RegularExpressions.Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ");
        }
    }
}

/// <summary>Recurso opcional do Windows (capability) instalado, ex.: idiomas, WordPad, Media Player.</summary>
public sealed class WindowsCapability
{
    public required string Name { get; init; }
    public bool IsLanguage => Name.StartsWith("Language.", StringComparison.OrdinalIgnoreCase);
    public string? LanguageTag { get; init; }

    /// <summary>Pacote de idioma que não está na lista de idiomas do usuário.</summary>
    public bool IsUnusedLanguage { get; init; }

    public string DisplayName => Name.Split('~')[0];
}

/// <summary>Apps da Store, bloatware e recursos opcionais (via PowerShell).</summary>
public static class StoreAndFeaturesService
{
    /// <summary>Apps pré-instalados que costumam ser removidos com segurança (podem ser reinstalados pela Store).</summary>
    private static readonly string[] BloatwarePrefixes =
    [
        "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingFinance", "Microsoft.BingSports", "Microsoft.BingSearch",
        "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.MicrosoftOfficeHub",
        "Microsoft.People", "Microsoft.WindowsFeedbackHub", "Microsoft.ZuneVideo", "Microsoft.ZuneMusic", "Microsoft.MixedReality.Portal",
        "Microsoft.SkypeApp", "Microsoft.Microsoft3DViewer", "Microsoft.Print3D", "Microsoft.3DBuilder", "Microsoft.Wallet",
        "Microsoft.WindowsMaps", "Microsoft.Messaging", "Microsoft.OneConnect", "Microsoft.549981C3F5F10", "Microsoft.PowerAutomateDesktop",
        "Microsoft.Todos", "Microsoft.MicrosoftStickyNotes", "Microsoft.YourPhone", "Microsoft.WindowsSoundRecorder", "Microsoft.WindowsAlarms",
        "Microsoft.Office.OneNote", "MicrosoftTeams", "Clipchamp.Clipchamp", "Microsoft.GamingApp", "Microsoft.XboxApp",
        "king.com.", "Disney.", "SpotifyAB.", "Facebook.", "BytedancePte.", "AmazonVideo.", "Microsoft.Advertising",
        "Microsoft.MicrosoftJournal", "Microsoft.OutlookForWindows", "MicrosoftCorporationII.MicrosoftFamily", "MicrosoftCorporationII.QuickAssist",
    ];

    public static async Task<List<StoreApp>> ListStoreAppsAsync(CancellationToken ct)
    {
        const string script =
            "Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { -not $_.IsFramework -and $_.SignatureKind -ne 'System' } | " +
            "Select-Object Name, PackageFullName, @{n='Version';e={\"$($_.Version)\"}}, InstallLocation, NonRemovable | ConvertTo-Json -Compress";
        var run = await CommandRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(2), cancellationToken: ct).ConfigureAwait(false);
        var apps = new List<StoreApp>();
        if (string.IsNullOrWhiteSpace(run.Output))
        {
            return apps;
        }

        try
        {
            using var doc = JsonDocument.Parse(run.Output.Trim());
            foreach (var a in JsonHelpers.Items(doc.RootElement))
            {
                string name = JsonHelpers.Str(a, "Name") ?? "?";
                apps.Add(new StoreApp
                {
                    Name = name,
                    FullName = JsonHelpers.Str(a, "PackageFullName") ?? name,
                    Version = JsonHelpers.Str(a, "Version"),
                    InstallLocation = JsonHelpers.Str(a, "InstallLocation"),
                    NonRemovable = JsonHelpers.Bool(a, "NonRemovable"),
                    IsBloatware = BloatwarePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)),
                });
            }
        }
        catch (JsonException)
        {
        }

        await Task.Run(() => Parallel.ForEach(apps, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            app => app.SizeBytes = app.InstallLocation is { } loc ? DiskMeasure.DirectorySize(loc, ct) : 0), ct).ConfigureAwait(false);
        return apps.OrderByDescending(a => a.IsBloatware).ThenByDescending(a => a.SizeBytes).ToList();
    }

    public static async Task<CommandResult> RemoveStoreAppAsync(StoreApp app, CancellationToken ct)
    {
        if (app.NonRemovable)
        {
            return new CommandResult(-1, string.Empty, "Este app faz parte do Windows e não pode ser removido.", false);
        }

        string script = $"Remove-AppxPackage -Package '{app.FullName.Replace("'", "''")}' -ErrorAction Stop; 'OK'";
        return await CommandRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(5), cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Recursos opcionais instalados (requer administrador). Marca idiomas fora da lista do usuário.</summary>
    public static async Task<List<WindowsCapability>> ListCapabilitiesAsync(CancellationToken ct)
    {
        const string script =
            "$langs = @((Get-WinUserLanguageList -ErrorAction SilentlyContinue).LanguageTag); " +
            "$caps = Get-WindowsCapability -Online -ErrorAction Stop | Where-Object State -eq 'Installed' | Select-Object Name; " +
            "@{ Languages = $langs; Capabilities = @($caps) } | ConvertTo-Json -Depth 3 -Compress";
        var run = await CommandRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(5), cancellationToken: ct).ConfigureAwait(false);
        var result = new List<WindowsCapability>();
        if (string.IsNullOrWhiteSpace(run.Output))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(run.Output.Trim());
            var languages = JsonHelpers.Array(doc.RootElement, "Languages").Select(l => l.GetString() ?? string.Empty)
                .Where(l => l.Length > 0).ToList();
            foreach (var c in JsonHelpers.Array(doc.RootElement, "Capabilities"))
            {
                string name = JsonHelpers.Str(c, "Name") ?? string.Empty;
                if (name.Length == 0)
                {
                    continue;
                }

                // Language.Basic~~~pt-BR~0.0.1.0 → pt-BR
                string? tag = name.StartsWith("Language.", StringComparison.OrdinalIgnoreCase) ? name.Split('~').ElementAtOrDefault(3) : null;
                bool unused = tag is not null && languages.Count > 0 &&
                              !languages.Any(l => l.Equals(tag, StringComparison.OrdinalIgnoreCase));
                result.Add(new WindowsCapability { Name = name, LanguageTag = tag, IsUnusedLanguage = unused });
            }
        }
        catch (JsonException)
        {
        }

        return result.OrderByDescending(c => c.IsUnusedLanguage).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Task<CommandResult> RemoveCapabilityAsync(WindowsCapability capability, CancellationToken ct) =>
        CommandRunner.RunPowerShellAsync($"Remove-WindowsCapability -Online -Name '{capability.Name.Replace("'", "''")}' -ErrorAction Stop | Out-Null; 'OK'",
            TimeSpan.FromMinutes(30), cancellationToken: ct);
}
