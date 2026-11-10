using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Programs;
using Headroom.Core.Settings;
using Headroom.Tests.TestHelpers;
using Microsoft.Win32;

namespace Headroom.Tests.Programs;

/// <summary>Testes que trocam a pasta de dados do app rodam sozinhos (não em paralelo com os outros).</summary>
[CollectionDefinition("DadosDoApp", DisableParallelization = true)]
public sealed class AppDataCollection
{
}

/// <summary>
/// Simula a desinstalação do "VLC media player" (VideoLAN) em pastas temporárias e numa chave de teste do
/// registro, com outro programa ainda instalado cujas pastas e chaves nunca podem ser sugeridas.
/// </summary>
[Collection("DadosDoApp")]
public sealed class ProgramLeftoverTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _keyBase = @"Software\HeadroomTests\" + Guid.NewGuid().ToString("N");
    private readonly string _programFiles;
    private readonly string _roaming;
    private readonly string _local;
    private readonly string _install;

    public ProgramLeftoverTests()
    {
        _programFiles = Directory.CreateDirectory(_temp.Combine("pf")).FullName;
        _roaming = Directory.CreateDirectory(_temp.Combine("roaming")).FullName;
        _local = Directory.CreateDirectory(_temp.Combine("local")).FullName;
        _install = System.IO.Path.Combine(_programFiles, "VideoLAN", "VLC");

        _temp.CreateFile(@"pf\VideoLAN\VLC\vlc.exe", 4096);
        _temp.CreateFile(@"pf\VideoLAN\VLC\plugins\codec.dll", 8192);
        _temp.CreateFile(@"roaming\vlc\vlcrc", 512);
        _temp.CreateFile(@"local\VideoLAN\VLC\cache.dat", 2048);
        _temp.CreateFile(@"pf\OtherApp\other.exe", 1024);
        _temp.CreateFile(@"roaming\OtherApp\settings.ini", 100);
        _temp.CreateFile(@"roaming\Microsoft\nao-mexer.txt", 100);

        using var software = Registry.CurrentUser.CreateSubKey(_keyBase + @"\Software");
        software.CreateSubKey(@"VideoLAN\VLC").SetValue("Versao", "3.0");
        software.CreateSubKey("OtherApp").SetValue("Versao", "1.0");
        software.CreateSubKey(@"Microsoft\Teste").SetValue("x", 1);

        using var run = Registry.CurrentUser.CreateSubKey(_keyBase + @"\Run");
        run.SetValue("VLC", $"\"{_install}\\vlc.exe\" --started-from-file");
        run.SetValue("Other", $"\"{_programFiles}\\OtherApp\\other.exe\"");
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keyBase, throwOnMissingSubKey: false);
        _temp.Dispose();
    }

    private InstalledProgram Vlc() => new()
    {
        Name = "VLC media player 3.0.20 (x64)",
        Publisher = "VideoLAN",
        InstallLocation = _install,
        RegistryKey = $@"HKEY_CURRENT_USER\{_keyBase}\Uninstall\VLC",
    };

    private InstalledProgram OtherApp() => new()
    {
        Name = "Other App",
        Publisher = "Other Co",
        InstallLocation = System.IO.Path.Combine(_programFiles, "OtherApp"),
        RegistryKey = $@"HKEY_CURRENT_USER\{_keyBase}\Uninstall\OtherApp",
    };

    private ProgramLeftoverScanner.LeftoverSearchLocations Locations() => new()
    {
        FolderRoots = new[] { (_programFiles, "Arquivos de Programas"), (_roaming, "AppData\\Roaming"), (_local, "AppData\\Local") },
        ShortcutRoots = Array.Empty<string>(),
        SoftwareKeys = new[] { (RegistryHive.CurrentUser, _keyBase + @"\Software") },
        RunKeys = new[] { (RegistryHive.CurrentUser, _keyBase + @"\Run") },
        IncludeAppPaths = false,
        IncludeTasks = false,
        IncludeServices = false,
        IncludeUninstallEntry = false,
    };

    [Fact]
    public async Task Find_ListsFoldersKeysAndStartupEntriesOfTheProgramOnly()
    {
        var footprint = ProgramFootprint.From(Vlc());
        Assert.Contains("vlc.exe", footprint.Executables);
        Assert.Contains("vlc", footprint.StrongTokens);

        var leftovers = await ProgramLeftoverScanner.FindAsync(footprint, new[] { OtherApp() }, Locations());

        // Pasta do fabricante que só tem o VLC (inclui a pasta de instalação), configurações e cache.
        Assert.Contains(leftovers, l => l.Kind == LeftoverKind.Folder && l.Path == System.IO.Path.Combine(_programFiles, "VideoLAN"));
        Assert.Contains(leftovers, l => l.Kind == LeftoverKind.Folder && l.Path == System.IO.Path.Combine(_roaming, "vlc"));
        Assert.Contains(leftovers, l => l.Kind == LeftoverKind.Folder && l.Path == System.IO.Path.Combine(_local, "VideoLAN"));
        Assert.Contains(leftovers, l => l.Kind == LeftoverKind.RegistryKey && l.Path.EndsWith(@"\Software\VideoLAN", StringComparison.Ordinal));
        Assert.Contains(leftovers, l => l.Kind == LeftoverKind.RegistryValue && l.ValueName == "VLC");

        // Nada do outro programa instalado, nada da Microsoft, nada repetido.
        Assert.DoesNotContain(leftovers, l => l.Display.Contains("OtherApp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(leftovers, l => l.Display.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(leftovers, l => l.Kind == LeftoverKind.Folder && l.Path == _install);
        Assert.All(leftovers.Where(l => l.Confidence == LeftoverConfidence.High), l => Assert.True(l.IsSelected));
        Assert.True(leftovers.First(l => l.Path.EndsWith("VideoLAN", StringComparison.Ordinal) && l.Kind == LeftoverKind.Folder).Size >= 4096 + 8192);
    }

    [Fact]
    public async Task Find_SkipsEverythingWhenTheProgramIsStillInstalled()
    {
        // Se a desinstalação foi cancelada, o próprio programa aparece na lista de instalados: nada é sugerido.
        var footprint = ProgramFootprint.From(Vlc());
        var stillThere = new InstalledProgram
        {
            Name = Vlc().Name,
            Publisher = "VideoLAN",
            InstallLocation = _install,
            RegistryKey = @"HKEY_CURRENT_USER\outra\chave",
        };
        var leftovers = await ProgramLeftoverScanner.FindAsync(footprint, new[] { OtherApp(), stillThere }, Locations());

        Assert.DoesNotContain(leftovers, l => l.Kind == LeftoverKind.Folder);
        Assert.DoesNotContain(leftovers, l => l.Kind == LeftoverKind.RegistryKey);
    }

    [Fact]
    public async Task Remove_RecyclesFilesAndBacksUpTheRegistryBeforeDeleting()
    {
        AppPaths.OverrideDataDirectory(_temp.Combine("dados"));
        try
        {
            var footprint = ProgramFootprint.From(Vlc());
            var leftovers = await ProgramLeftoverScanner.FindAsync(footprint, new[] { OtherApp() }, Locations());
            var recycler = new MoveToBinRecycler(Directory.CreateDirectory(_temp.Combine("lixeira")).FullName);

            var result = await ProgramLeftoverRemover.RemoveAsync(leftovers.Where(l => l.IsSelected).ToList(), "VLC", isAdministrator: false,
                DeletionMode.RecycleBin, recycler);

            Assert.Empty(result.Errors);
            Assert.False(Directory.Exists(System.IO.Path.Combine(_programFiles, "VideoLAN")));
            Assert.False(Directory.Exists(System.IO.Path.Combine(_roaming, "vlc")));
            Assert.True(Directory.Exists(System.IO.Path.Combine(_programFiles, "OtherApp")));
            Assert.True(recycler.Recycled.Count >= 3);

            using var software = Registry.CurrentUser.OpenSubKey(_keyBase + @"\Software")!;
            Assert.Null(software.OpenSubKey("VideoLAN"));
            Assert.NotNull(software.OpenSubKey("OtherApp"));
            using var run = Registry.CurrentUser.OpenSubKey(_keyBase + @"\Run")!;
            Assert.Null(run.GetValue("VLC"));
            Assert.NotNull(run.GetValue("Other"));

            // Backup .reg do registro (desfazível na tela Registro) e log da operação.
            Assert.Contains(RegistryCleanupBackups(), f => File.ReadAllText(f).Contains("VideoLAN", StringComparison.Ordinal));
            Assert.True(File.Exists(result.LogPath));
        }
        finally
        {
            AppPaths.OverrideDataDirectory(null);
        }
    }

    [Theory]
    [InlineData("VLC media player 3.0.20 (x64)", "VideoLAN", "vlcmediaplayer")]
    [InlineData("7-Zip 23.01 (x64)", "Igor Pavlov", "7zip")]
    [InlineData("Microsoft Teams", "Microsoft Corporation", "teams")]
    [InlineData("Mozilla Thunderbird (x64 pt-BR)", "Mozilla", "thunderbird")]
    public void Tokens_IgnoreVersionsArchitectureAndPublisherPrefix(string name, string publisher, string expected)
    {
        var (strong, _, _) = ProgramLeftoverScanner.BuildTokens(name, publisher, null);
        Assert.Contains(expected, strong);
        Assert.DoesNotContain("microsoft", strong);
    }

    private IEnumerable<string> RegistryCleanupBackups() =>
        Directory.EnumerateFiles(AppPaths.RegistryBackupsDirectory, "*.reg", SearchOption.AllDirectories);

    /// <summary>"Lixeira" de teste: move para uma pasta em vez de usar a Lixeira do Windows.</summary>
    private sealed class MoveToBinRecycler(string bin) : IRecycler
    {
        public List<string> Recycled { get; } = new();

        public void RecycleFile(string path)
        {
            File.Move(path, System.IO.Path.Combine(bin, Guid.NewGuid().ToString("N")));
            Recycled.Add(path);
        }

        public void RecycleDirectory(string path)
        {
            Directory.Move(path, System.IO.Path.Combine(bin, Guid.NewGuid().ToString("N")));
            Recycled.Add(path);
        }
    }
}
