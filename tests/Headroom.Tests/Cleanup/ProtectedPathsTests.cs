using Headroom.Core.Cleanup.Safety;

namespace Headroom.Tests.Cleanup;

/// <summary>Garante que a lista negra fixa bloqueia os caminhos críticos do sistema real.</summary>
public class ProtectedPathsTests
{
    private static readonly ProtectedPaths Policy = ProtectedPaths.Default;
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static TheoryData<string> CriticalPaths()
    {
        var data = new TheoryData<string>
        {
            Path.Combine(Windows, "System32", "kernel32.dll"),
            Path.Combine(Windows, "System32", "drivers", "etc", "hosts"),
            Path.Combine(Windows, "System32"),
            Path.Combine(Windows, "SysWOW64", "ntdll.dll"),
            Path.Combine(Windows, "WinSxS", "amd64_x", "a.dll"),
            Path.Combine(Windows, "WinSxS"),
            Path.Combine(Windows, "Installer", "abc.msi"),
            Path.Combine(ProgramData, "Package Cache", "{guid}", "setup.exe"),
            @"C:\pagefile.sys",
            @"C:\swapfile.sys",
            @"C:\hiberfil.sys",
            @"D:\pagefile.sys",
            @"C:\",
            @"D:\",
            "C:",
            @"C:\System Volume Information\x",
            @"C:\$Recycle.Bin\S-1-5-21\file.txt",
            Windows,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LocalAppData,
            Path.Combine(Windows, "Temp", "..", "System32", "x.dll"),
            @"\\?\" + Path.Combine(Windows, "System32", "x.dll"),
            Path.Combine(Windows, "System32").ToUpperInvariant() + @"\X.DLL",
            Path.Combine(Windows, "System32") + "\\",
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(CriticalPaths))]
    public void Check_BlocksCriticalSystemPaths(string path)
    {
        Assert.True(Policy.Check(path).IsProtected, $"Deveria estar protegido: {path}");
    }

    [Theory]
    [MemberData(nameof(CriticalPaths))]
    public void Check_BlocksCriticalPathsEvenWithBrowserPrivacyAllowed(string path)
    {
        Assert.True(Policy.Check(path, allowBrowserPrivacyData: true).IsProtected);
    }

    [Fact]
    public void Check_AllowsOrdinaryCleanupLocations()
    {
        Assert.False(Policy.Check(Path.Combine(Path.GetTempPath(), "arquivo.tmp")).IsProtected);
        Assert.False(Policy.Check(Path.Combine(Windows, "Temp", "x.log")).IsProtected);
        Assert.False(Policy.Check(Path.Combine(Windows, "SoftwareDistribution", "Download", "abc")).IsProtected);
        Assert.False(Policy.Check(@"D:\Projetos\app\node_modules").IsProtected);
        Assert.False(Policy.Check(@"C:\Windows.old").IsProtected);
    }

    [Fact]
    public void Check_BlocksFoldersThatContainProtectedPaths()
    {
        Assert.True(Policy.Check(Path.Combine(LocalAppData, "Google")).IsProtected);
        Assert.True(Policy.Check(Path.Combine(RoamingAppData, "Mozilla")).IsProtected);
        Assert.True(Policy.Check(Path.Combine(ProgramData)).IsProtected);
    }

    [Fact]
    public void Check_BrowserProfilesOnlyAllowCacheFolders()
    {
        string chrome = Path.Combine(LocalAppData, "Google", "Chrome", "User Data");

        Assert.False(Policy.Check(Path.Combine(chrome, "Default", "Cache", "Cache_Data", "f_000001")).IsProtected);
        Assert.False(Policy.Check(Path.Combine(chrome, "Default", "Code Cache", "js", "index")).IsProtected);
        Assert.False(Policy.Check(Path.Combine(chrome, "Profile 2", "Service Worker", "CacheStorage", "x")).IsProtected);
        Assert.False(Policy.Check(Path.Combine(chrome, "ShaderCache", "data_0")).IsProtected);

        Assert.True(Policy.Check(chrome).IsProtected);
        Assert.True(Policy.Check(Path.Combine(chrome, "Default")).IsProtected);
        Assert.True(Policy.Check(Path.Combine(chrome, "Default", "Login Data")).IsProtected);
        Assert.True(Policy.Check(Path.Combine(chrome, "Default", "Bookmarks")).IsProtected);
        Assert.True(Policy.Check(Path.Combine(chrome, "Default", "Extensions", "abc", "1.0", "x.js")).IsProtected);
        Assert.True(Policy.Check(Path.Combine(chrome, "Default", "History")).IsProtected);
    }

    [Fact]
    public void Check_BrowserPrivacyDataNeedsExplicitOptInAndNeverIncludesPasswords()
    {
        string edge = Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data", "Default");
        string firefox = Path.Combine(RoamingAppData, "Mozilla", "Firefox", "Profiles", "abc.default-release");

        Assert.False(Policy.Check(Path.Combine(edge, "History"), allowBrowserPrivacyData: true).IsProtected);
        Assert.False(Policy.Check(Path.Combine(edge, "Network", "Cookies"), allowBrowserPrivacyData: true).IsProtected);
        Assert.False(Policy.Check(Path.Combine(firefox, "cookies.sqlite"), allowBrowserPrivacyData: true).IsProtected);
        Assert.False(Policy.Check(Path.Combine(firefox, "sessionstore-backups", "recovery.jsonlz4"), allowBrowserPrivacyData: true).IsProtected);

        Assert.True(Policy.Check(Path.Combine(edge, "Login Data"), allowBrowserPrivacyData: true).IsProtected);
        Assert.True(Policy.Check(Path.Combine(edge, "Bookmarks"), allowBrowserPrivacyData: true).IsProtected);
        Assert.True(Policy.Check(Path.Combine(firefox, "places.sqlite"), allowBrowserPrivacyData: true).IsProtected);
        Assert.True(Policy.Check(Path.Combine(firefox, "logins.json"), allowBrowserPrivacyData: true).IsProtected);
        Assert.True(Policy.Check(Path.Combine(firefox, "key4.db"), allowBrowserPrivacyData: true).IsProtected);
        Assert.True(Policy.Check(Path.Combine(firefox, "extensions", "x.xpi"), allowBrowserPrivacyData: true).IsProtected);
    }

    [Fact]
    public void Check_ExpandsShortNames()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        const string shortName = @"C:\PROGRA~1";
        if (!Directory.Exists(shortName) || !programFiles.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
        {
            return; // Nomes 8.3 desativados neste volume: nada a verificar.
        }

        Assert.True(Policy.Check(shortName).IsProtected);
    }

    [Fact]
    public void Check_RejectsRelativeAndEmptyPaths()
    {
        Assert.True(Policy.Check("").IsProtected);
        Assert.True(Policy.Check("   ").IsProtected);
    }
}
