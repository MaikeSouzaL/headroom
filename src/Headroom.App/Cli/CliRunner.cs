using System.Runtime.InteropServices;
using Headroom.Core.Cleanup;
using Headroom.Core.Cleanup.Catalog;
using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cli;
using Headroom.Core.Drives;
using Headroom.Core.Formatting;
using Headroom.Core.History;
using Headroom.Core.Scanning;
using Headroom.Core.Settings;

namespace Headroom.App.Cli;

/// <summary>
/// Modo linha de comando (sem janela principal): limpeza por perfil, verificação de espaço livre e scan.
/// Usado pela limpeza agendada: Headroom.exe --profile rapida --silent --log --notify
/// </summary>
internal static class CliRunner
{
    private const int AttachParentProcess = -1;

    public static int Run(CommandLineOptions options)
    {
        AttachConsole(AttachParentProcess);
        Console.WriteLine();

        if (options.Errors.Count > 0)
        {
            foreach (var error in options.Errors)
            {
                Console.Error.WriteLine("Erro: " + error);
            }

            Console.WriteLine(CommandLineOptions.Usage);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(CommandLineOptions.Usage);
            return 0;
        }

        try
        {
            if (options.ScanPath is { } path)
            {
                return RunScan(path).GetAwaiter().GetResult();
            }

            if (options.CheckSpace)
            {
                return CheckSpace(options);
            }

            if (options.Profile is { } profile)
            {
                return RunCleanup(profile, options).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Erro: " + Services.DialogService.FriendlyMessage(ex));
            App.LogCrash(ex);
            if (options.Notify)
            {
                NotificationWindow.ShowAndWait("Headroom — erro", Services.DialogService.FriendlyMessage(ex));
            }

            return 1;
        }

        return 0;
    }

    private static async Task<int> RunScan(string path)
    {
        Console.WriteLine($"Escaneando {path}…");
        var progress = new Progress<ScanProgress>(p =>
            Console.Write($"\r{ByteSize.FormatCount(p.FilesScanned)} arquivos, {ByteSize.Format(p.BytesCounted)}, {ByteSize.FormatDuration(p.Elapsed)}      "));
        var result = await new DiskScanner().ScanAsync(path, new ScanOptions(), progress);
        Console.WriteLine();
        Console.WriteLine($"Total: {ByteSize.Format(result.TotalBytes)} em {ByteSize.FormatCount(result.FileCount)} arquivos e " +
                          $"{ByteSize.FormatCount(result.DirectoryCount)} pastas ({ByteSize.FormatDuration(result.Elapsed)}, " +
                          $"{result.FileCount / Math.Max(0.001, result.Elapsed.TotalSeconds):N0} arquivos/s).");
        Console.WriteLine("Maiores pastas:");
        foreach (var node in result.Root.Children.Where(c => c.IsDirectory).Take(10))
        {
            Console.WriteLine($"  {ByteSize.Format(node.Size),10}  {node.Name}");
        }

        Console.WriteLine("Por categoria:");
        foreach (var c in result.Categories.Where(c => c.TotalBytes > 0))
        {
            Console.WriteLine($"  {ByteSize.Format(c.TotalBytes),10}  {c.DisplayName}");
        }

        new HistoryStore().AddScan(result);
        return 0;
    }

    private static int CheckSpace(CommandLineOptions options)
    {
        var settings = new SettingsStore().Load();
        new HistoryStore().RecordFreeSpace();
        double threshold = settings.FreeSpaceAlertPercent / 100.0;
        var low = DriveService.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.FreeFraction < threshold)
            .ToList();

        foreach (var drive in DriveService.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            Console.WriteLine($"{drive.Letter} {ByteSize.Format(drive.FreeBytes)} livres de {ByteSize.Format(drive.TotalBytes)} ({ByteSize.FormatPercent(drive.FreeFraction)})");
        }

        if (low.Count > 0 && settings.FreeSpaceAlertEnabled && options.Notify)
        {
            NotificationWindow.ShowAndWait("Pouco espaço livre",
                string.Join("\n", low.Select(d => $"{d.Letter}: {ByteSize.Format(d.FreeBytes)} livres ({ByteSize.FormatPercent(d.FreeFraction)})")) +
                "\nAbra o Headroom para liberar espaço.");
        }

        return 0;
    }

    private static async Task<int> RunCleanup(string profileId, CommandLineOptions options)
    {
        var settings = new SettingsStore().Load();
        bool admin = DiskScanner.IsElevated();
        var catalog = RuleCatalog.Create(settings, admin);
        var profile = CleanupProfiles.Find(profileId, settings.CustomProfileRuleIds);
        var context = new CleanupContext(settings, admin);
        var rules = catalog.Rules.Where(r => profile.Includes(r) && SafeApplicable(r, context)).ToList();

        Console.WriteLine($"Headroom — perfil {profile.Name}: {rules.Count} limpezas ({(admin ? "administrador" : "sem administrador")}).");
        var engine = new CleanupEngine(context);
        var analyses = await engine.AnalyzeAsync(rules);
        long planned = analyses.Where(a => a.HasWork).Sum(a => a.SelectedBytes);
        Console.WriteLine($"Análise: cerca de {ByteSize.Format(planned)} a limpar.");

        var progress = new Progress<CleanupProgress>(p =>
        {
            if (!options.Silent)
            {
                Console.WriteLine($"[{p.Fraction:P0}] {p.RuleName}: {p.Detail}");
            }
        });

        var summary = await engine.CleanAsync(analyses, new CleanupRunOptions
        {
            Mode = options.Permanent ? DeletionMode.Permanent : DeletionMode.RecycleBin,
            CreateRestorePoint = settings.CreateRestorePointBeforeAdvanced,
            IsAdministrator = admin,
            LogPrefix = "limpeza-cli",
            OnProcessesRunning = null, // silencioso: nunca fecha programas
        }, progress);

        new HistoryStore().AddCleanup(new CleanupHistoryEntry(DateTime.Now, summary.TotalBytesFreed, summary.BytesMovedToRecycleBin,
            summary.Results.Count, summary.TotalSkipped, summary.TotalErrors, summary.LogPath, options.Silent ? "Agendada" : "Linha de comando"));

        Console.WriteLine(summary.Headline);
        foreach (var (category, bytes) in summary.BytesByCategory.Where(kv => kv.Value > 0))
        {
            Console.WriteLine($"  {CleanupCategoryInfo.DisplayName(category)}: {ByteSize.Format(bytes)}");
        }

        Console.WriteLine($"Pulados: {summary.TotalSkipped}. Erros: {summary.TotalErrors}.");
        foreach (var skipped in summary.SkippedRules)
        {
            Console.WriteLine("  Pulada: " + skipped);
        }

        if (options.Log || options.Silent)
        {
            Console.WriteLine("Log: " + summary.LogPath);
        }

        if (options.Notify)
        {
            NotificationWindow.ShowAndWait("Limpeza concluída", summary.Headline +
                (summary.TotalSkipped > 0 ? $"\n{summary.TotalSkipped} item(ns) em uso foram pulados." : string.Empty));
        }

        return summary.TotalErrors > 0 && summary.TotalBytesFreed == 0 ? 1 : 0;
    }

    private static bool SafeApplicable(ICleanupRule rule, CleanupContext context)
    {
        try
        {
            return rule.IsApplicable(context);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
