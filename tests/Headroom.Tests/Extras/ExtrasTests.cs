using Headroom.Core.Cleanup.Declarative;
using Headroom.Core.Cli;
using Headroom.Core.History;
using Headroom.Core.Optimization;
using Headroom.Core.Programs;
using Headroom.Core.RegistryCleanup;
using Headroom.Core.Reports;
using Headroom.Core.Scanning;
using Headroom.Core.Scheduling;
using Headroom.Core.Settings;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Extras;

public class CommandLineTests
{
    [Fact]
    public void Parse_ProfileSilentLog()
    {
        var o = CommandLineOptions.Parse(["--profile", "rapida", "--silent", "--log"]);
        Assert.True(o.IsCommandLineMode);
        Assert.Equal("rapida", o.Profile);
        Assert.True(o.Silent);
        Assert.True(o.Log);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_AcceptsShortAndPortugueseAliases()
    {
        var o = CommandLineOptions.Parse(["-p", "DEV", "-s", "--notificar", "--permanente"]);
        Assert.Equal("dev", o.Profile);
        Assert.True(o.Silent);
        Assert.True(o.Notify);
        Assert.True(o.Permanent);
    }

    [Fact]
    public void Parse_ReportsErrors()
    {
        Assert.NotEmpty(CommandLineOptions.Parse(["--profile", "turbo"]).Errors);
        Assert.NotEmpty(CommandLineOptions.Parse(["--profile"]).Errors);
        Assert.NotEmpty(CommandLineOptions.Parse(["--desconhecido"]).Errors);
    }

    [Fact]
    public void Parse_NoArgumentsOpensInterface()
    {
        Assert.False(CommandLineOptions.Parse(Array.Empty<string>()).IsCommandLineMode);
    }

    [Fact]
    public void Schedule_BuildsSchtasksArguments()
    {
        string args = ScheduledTasks.BuildCleanupArguments(@"C:\Program Files\Headroom\Headroom.exe", ScheduleFrequency.Weekly, "9:30", "rapida", true);

        Assert.Contains("/TN \"Headroom\\Limpeza agendada\"", args);
        Assert.Contains("\\\"C:\\Program Files\\Headroom\\Headroom.exe\\\" --profile rapida --silent --log --notify", args);
        Assert.Contains("/SC WEEKLY /D MON", args);
        Assert.Contains("/ST 09:30", args);
        Assert.EndsWith("/RL HIGHEST", args);
        Assert.Throws<ArgumentException>(() => ScheduledTasks.BuildCleanupArguments("x.exe", ScheduleFrequency.Daily, "25:99", "rapida", false));
    }
}

public class ParsingTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Tools\tool.exe /background", @"C:\Tools\tool.exe")]
    [InlineData(@"C:\Program Files\Some App\run.exe -x", @"C:\Program Files\Some App\run.exe")]
    [InlineData("", null)]
    public void StartupManager_ExtractsExecutable(string command, string? expected)
    {
        Assert.Equal(expected, StartupManager.ExtractExecutable(command));
    }

    [Fact]
    public void UninstallString_ParsesMsiAndQuotedPaths()
    {
        var (file, args) = InstalledProgramsService.ParseUninstallString("MsiExec.exe /I{12345678-1234-1234-1234-1234567890AB}");
        Assert.EndsWith("msiexec.exe", file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/X{12345678-1234-1234-1234-1234567890AB}", args);

        var (file2, args2) = InstalledProgramsService.ParseUninstallString("\"C:\\Program Files\\App\\unins000.exe\" /SILENT");
        Assert.Equal(@"C:\Program Files\App\unins000.exe", file2);
        Assert.Equal("/SILENT", args2);
    }

    [Fact]
    public void InstallDate_ParsesRegistryFormat()
    {
        Assert.Equal(new DateTime(2024, 3, 15), InstalledProgramsService.ParseInstallDate("20240315"));
        Assert.Null(InstalledProgramsService.ParseInstallDate("15/03/2024"));
        Assert.Null(InstalledProgramsService.ParseInstallDate(null));
    }

    [Fact]
    public void OrphanDetector_NormalizesNames()
    {
        Assert.Equal("corsairicue4", OrphanFolderDetector.Normalize("Corsair iCUE 4"));
        Assert.Equal("7zip", OrphanFolderDetector.Normalize("7-Zip"));
    }

    [Fact]
    public void RegistryCleaner_OnlyTreatsMissingLocalPathsAsOrphans()
    {
        using var temp = new TempDirectory();
        string existing = temp.CreateFile("app.exe", 1);

        Assert.False(RegistryCleaner.IsMissingLocalPath(existing));
        Assert.True(RegistryCleaner.IsMissingLocalPath(temp.Combine("sumiu.exe")));
        Assert.False(RegistryCleaner.IsMissingLocalPath(@"\\servidor\share\app.exe"));
        Assert.False(RegistryCleaner.IsMissingLocalPath("relativo.exe"));
        Assert.False(RegistryCleaner.IsMissingLocalPath(@"Q:\disco-que-nao-existe\app.exe"));
    }

    [Fact]
    public void ShippedJsonRules_AreValid()
    {
        string dir = AppContext.BaseDirectory;
        string? root = dir;
        while (root is not null && !File.Exists(Path.Combine(root, "Headroom.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var (rules, errors) = JsonRuleLoader.LoadDirectories([Path.Combine(root!, "Rules")]);
        Assert.Empty(errors);
        Assert.True(rules.Count >= 3);
    }
}

public class ReportAndStoreTests
{
    private static async Task<ScanResult> SampleScan(TempDirectory temp)
    {
        temp.CreateFile(@"videos\filme.mp4", 5000);
        temp.CreateFile(@"docs\relatorio;final.pdf", 1200);
        temp.CreateFile(@"proj\app.csproj", 10);
        temp.CreateFile(@"proj\bin\app.dll", 700);
        return await new DiskScanner().ScanAsync(temp.Path);
    }

    [Fact]
    public async Task HtmlReport_ContainsSummaryAndEscapesPaths()
    {
        using var temp = new TempDirectory();
        var result = await SampleScan(temp);
        string file = temp.Combine("saida", "relatorio.html");

        HtmlReportExporter.Export(result, file);

        string html = File.ReadAllText(file);
        Assert.Contains("<!doctype html>", html);
        Assert.Contains("Relatório de espaço", html);
        Assert.Contains("filme.mp4", html);
        Assert.Contains("Vídeos", html);
        Assert.Contains("Artefatos de desenvolvimento", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public async Task CsvReport_UsesSemicolonsAndQuotesWhenNeeded()
    {
        using var temp = new TempDirectory();
        var result = await SampleScan(temp);
        string file = temp.Combine("relatorio.csv");

        CsvReportExporter.Export(result, file);

        var lines = File.ReadAllLines(file);
        Assert.Equal("Tipo;Caminho;Tamanho (bytes);Tamanho;Arquivos;% da pasta-pai;Modificado;Categoria", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("Pasta;", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Arquivo;", StringComparison.Ordinal) && l.Contains("\"") && l.Contains("relatorio;final.pdf"));
        Assert.Equal("\"a;b\"", CsvReportExporter.Escape("a;b"));
        Assert.Equal("\"diz \"\"oi\"\"\"", CsvReportExporter.Escape("diz \"oi\""));
    }

    [Fact]
    public async Task HistoryStore_PersistsScansCleanupsAndFreeSpace()
    {
        using var temp = new TempDirectory();
        var store = new HistoryStore(temp.Combine("historico.json"));
        var result = await SampleScan(temp);

        store.AddScan(result);
        store.AddCleanup(new CleanupHistoryEntry(DateTime.Now, 1234, 1000, 3, 1, 0, null, "Teste"));
        var data = store.Load();

        Assert.Single(data.Scans);
        Assert.Equal(result.TotalBytes, data.Scans[0].TotalBytes);
        Assert.Single(data.Cleanups);
        Assert.NotEmpty(data.FreeSpace);
    }

    [Fact]
    public void SettingsStore_RoundTripsAndClampsValues()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.Combine("settings.json"));
        var settings = new AppSettings { Theme = AppTheme.Dark, TopFilesCount = 999_999, ExcludedPaths = { @"D:\Backups" } };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(100_000, loaded.TopFilesCount);
        Assert.Equal(@"D:\Backups", Assert.Single(loaded.ExcludedPaths));
    }

    [Fact]
    public void SettingsStore_ReturnsDefaultsForCorruptFile()
    {
        using var temp = new TempDirectory();
        string path = temp.CreateFileWithContent("settings.json", "{ isto não é json"u8.ToArray());

        var loaded = new SettingsStore(path).Load();

        Assert.Equal(AppTheme.System, loaded.Theme);
        Assert.Equal(500, loaded.TopFilesCount);
    }
}
