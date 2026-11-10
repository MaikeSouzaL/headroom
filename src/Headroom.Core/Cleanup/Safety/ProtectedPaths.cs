using System.Runtime.InteropServices;
using System.Text;

namespace Headroom.Core.Cleanup.Safety;

/// <summary>Resultado da verificação de um caminho contra a lista negra.</summary>
public readonly record struct ProtectionResult(bool IsProtected, string Reason)
{
    public static readonly ProtectionResult Allowed = new(false, string.Empty);

    public static ProtectionResult Blocked(string reason) => new(true, reason);
}

/// <summary>
/// Lista negra fixa de caminhos que nenhuma regra (nem o usuário pela interface) consegue apagar:
/// System32, SysWOW64, WinSxS, Windows\Installer, ProgramData\Package Cache, pagefile/swapfile/hiberfil,
/// raiz de qualquer disco, pastas essenciais do sistema/usuário e, dentro de perfis de navegador,
/// tudo que não for cache (senhas, favoritos, extensões nunca são tocados).
/// </summary>
public sealed class ProtectedPaths
{
    /// <summary>Subpastas de perfis de navegador que podem ser limpas (cache).</summary>
    private static readonly HashSet<string> BrowserCacheSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "GraphiteDawnCache",
        "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "CacheStorage", "ScriptCache",
        "cache2", "startupCache", "thumbnails", "component_crx_cache", "Media Cache", "jumpListCache",
        "OfflineCache", "shader-cache",
    };

    /// <summary>
    /// Arquivos de perfil de navegador que só podem ser apagados por regras opcionais de privacidade
    /// (histórico, cookies, sessões). Nunca inclui senhas, favoritos ou extensões.
    /// </summary>
    private static readonly HashSet<string> BrowserPrivacyFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "History", "History-journal", "Visited Links", "Top Sites", "Top Sites-journal",
        "Cookies", "Cookies-journal", "Current Session", "Last Session", "Current Tabs", "Last Tabs",
        "Network Action Predictor", "Network Action Predictor-journal", "Shortcuts", "Shortcuts-journal",
        "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm", "formhistory.sqlite",
        "formhistory.sqlite-wal", "sessionstore.jsonlz4", "sessionstore.js",
    };

    /// <summary>Pastas de sessão que podem ser apagadas por inteiro pela regra opcional de sessões.</summary>
    private static readonly HashSet<string> BrowserPrivacyFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sessions", "sessionstore-backups",
    };

    /// <summary>Itens de perfil que nunca podem ser apagados, nem dentro de pastas de cache.</summary>
    private static readonly HashSet<string> BrowserNeverDelete = new(StringComparer.OrdinalIgnoreCase)
    {
        "Login Data", "Login Data-journal", "Login Data For Account", "Bookmarks", "Bookmarks.bak",
        "Extensions", "Local Extension Settings", "Extension State", "Extension Rules", "Extension Scripts",
        "Preferences", "Secure Preferences", "Web Data", "Local State", "logins.json", "key4.db", "key3.db",
        "places.sqlite", "favicons.sqlite", "extensions", "extensions.json", "prefs.js", "cert9.db",
        "Wallet", "Payment", "Account Web Data",
    };

    private static readonly HashSet<string> CriticalFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "swapfile.sys", "hiberfil.sys", "bootmgr", "BOOTNXT", "BOOTSECT.BAK",
    };

    private static readonly HashSet<string> RootSystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI", "$WinREAgent", "Config.Msi",
    };

    private readonly List<string> _protectedSubtrees = new();
    private readonly HashSet<string> _protectedExact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _browserProfileRoots = new();

    /// <summary>Política para o sistema atual (lê as pastas do ambiente).</summary>
    public static ProtectedPaths Default { get; } = new(SystemLocations.FromEnvironment());

    public ProtectedPaths(SystemLocations locations)
    {
        string win = locations.WindowsDirectory;

        // Subárvores inteiras protegidas (nada dentro delas pode ser apagado por arquivo).
        foreach (var sub in new[]
                 {
                     "System32", "SysWOW64", "SysArm32", "WinSxS", "Installer", "servicing", "Boot", "assembly",
                     "Microsoft.NET", "SystemApps", "SystemResources", "Fonts", "PolicyDefinitions", "INF",
                     "ImmersiveControlPanel", "Resources", "WaaS",
                 })
        {
            AddSubtree(Path.Combine(win, sub));
        }

        AddSubtree(Path.Combine(locations.ProgramData, "Package Cache"));
        AddSubtree(Path.Combine(locations.ProgramData, "Microsoft", "Crypto"));
        AddSubtree(Path.Combine(locations.ProgramData, "Microsoft", "Windows", "Start Menu"));
        AddSubtree(Path.Combine(locations.ProgramFiles, "WindowsApps"));
        AddSubtree(Path.Combine(locations.ProgramFiles, "Windows Defender"));
        AddSubtree(Path.Combine(locations.ProgramFiles, "Common Files"));
        if (!string.IsNullOrEmpty(locations.ProgramFilesX86))
        {
            AddSubtree(Path.Combine(locations.ProgramFilesX86, "Common Files"));
        }

        // A própria pasta não pode ser apagada (mas conteúdo específico dentro dela pode).
        foreach (var exact in new[]
                 {
                     win, Path.Combine(win, "Temp"), locations.ProgramFiles, locations.ProgramFilesX86,
                     locations.ProgramData, locations.UsersDirectory, locations.UserProfile, locations.TempDirectory,
                     Path.Combine(locations.UserProfile, "AppData"), locations.LocalAppData, locations.RoamingAppData,
                     Path.Combine(locations.UserProfile, "AppData", "LocalLow"), locations.Desktop, locations.Documents,
                     locations.Downloads, locations.Pictures, locations.Videos, locations.Music,
                     Path.Combine(win, "SoftwareDistribution"), Path.Combine(win, "Logs"),
                     Path.Combine(locations.ProgramData, "Microsoft"), Path.Combine(locations.ProgramData, "Microsoft", "Windows"),
                     Path.Combine(locations.LocalAppData, "Microsoft"), Path.Combine(locations.RoamingAppData, "Microsoft"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(exact))
            {
                _protectedExact.Add(Normalize(exact));
            }
        }

        foreach (var root in locations.BrowserProfileRoots)
        {
            _browserProfileRoots.Add(Normalize(root));
        }
    }

    private void AddSubtree(string path) => _protectedSubtrees.Add(Normalize(path));

    /// <summary>
    /// Verifica um caminho. <paramref name="allowBrowserPrivacyData"/> libera arquivos de histórico/cookies/
    /// sessões (regras opcionais), mas nunca senhas, favoritos ou extensões.
    /// </summary>
    public ProtectionResult Check(string path, bool allowBrowserPrivacyData = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ProtectionResult.Blocked("Caminho vazio.");
        }

        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ProtectionResult.Blocked("Caminho inválido.");
        }

        if (!Path.IsPathFullyQualified(full))
        {
            return ProtectionResult.Blocked("Caminho relativo não é permitido.");
        }

        string? root = Path.GetPathRoot(full);
        if (root is null || full.Length <= root.TrimEnd('\\').Length + 1)
        {
            return ProtectionResult.Blocked("A raiz de um disco nunca é apagada.");
        }

        string name = Path.GetFileName(full);
        if (CriticalFileNames.Contains(name))
        {
            return ProtectionResult.Blocked($"\"{name}\" é um arquivo do sistema (use as opções de hibernação/memória virtual do Windows).");
        }

        // Pastas especiais na raiz do disco (ex.: C:\System Volume Information, C:\$Recycle.Bin).
        string relativeToRoot = full[root.TrimEnd('\\').Length..].TrimStart('\\');
        string firstSegment = relativeToRoot.Split('\\', 2)[0];
        if (RootSystemFolders.Contains(firstSegment))
        {
            return ProtectionResult.Blocked($"\"{firstSegment}\" é uma pasta protegida do sistema.");
        }

        if (_protectedExact.Contains(full))
        {
            return ProtectionResult.Blocked("Esta pasta é essencial e não pode ser apagada (só o conteúdo de regras específicas).");
        }

        foreach (var subtree in _protectedSubtrees)
        {
            if (IsSameOrInside(full, subtree))
            {
                return ProtectionResult.Blocked($"Caminho protegido: {subtree}");
            }
        }

        foreach (var browserRoot in _browserProfileRoots)
        {
            if (IsSameOrInside(full, browserRoot))
            {
                return CheckBrowserPath(full, browserRoot, allowBrowserPrivacyData);
            }
        }

        // Apagar uma pasta que CONTÉM algo protegido também é proibido
        // (ex.: apagar "AppData\Local\Google" levaria junto o perfil do Chrome).
        foreach (var protectedPath in _protectedSubtrees.Concat(_protectedExact).Concat(_browserProfileRoots))
        {
            if (protectedPath.Length > full.Length && IsSameOrInside(protectedPath, full))
            {
                return ProtectionResult.Blocked($"A pasta contém um caminho protegido: {protectedPath}");
            }
        }

        return ProtectionResult.Allowed;
    }

    private static ProtectionResult CheckBrowserPath(string full, string browserRoot, bool allowPrivacy)
    {
        if (full.Length == browserRoot.Length)
        {
            return ProtectionResult.Blocked("A pasta de perfis do navegador nunca é apagada.");
        }

        var segments = full[(browserRoot.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (BrowserNeverDelete.Contains(segment))
            {
                return ProtectionResult.Blocked("Senhas, favoritos, extensões e configurações do navegador nunca são apagados.");
            }
        }

        // Liberado se o item for uma pasta de cache ou estiver dentro de uma (inclui
        // "Service Worker\CacheStorage" e caches na raiz de "User Data", como ShaderCache).
        foreach (var segment in segments)
        {
            if (BrowserCacheSegments.Contains(segment))
            {
                return ProtectionResult.Allowed;
            }
        }

        if (allowPrivacy)
        {
            string last = segments[^1];
            if (BrowserPrivacyFiles.Contains(last) || segments.Any(s => BrowserPrivacyFolders.Contains(s)))
            {
                return ProtectionResult.Allowed;
            }
        }

        return ProtectionResult.Blocked("Dentro do perfil do navegador só as pastas de cache podem ser limpas.");
    }

    /// <summary>true se <paramref name="path"/> é igual a <paramref name="root"/> ou está dentro dela.</summary>
    public static bool IsSameOrInside(string path, string root)
    {
        if (path.Length < root.Length || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == root.Length || path[root.Length] == '\\' || root.EndsWith('\\');
    }

    /// <summary>
    /// Normaliza: caminho completo, sem prefixo \\?\, sem barra final, nomes curtos (8.3) expandidos e
    /// ".." resolvido — impede escapar da lista negra com truques de caminho.
    /// </summary>
    public static string Normalize(string path)
    {
        string p = path.Trim().Trim('"');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            p = @"\\" + p[8..];
        }
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        p = p.Replace('/', '\\');
        if (p.Length == 2 && p[1] == ':')
        {
            p += "\\";
        }

        p = Path.GetFullPath(p);
        if (p.Contains('~'))
        {
            p = ExpandShortName(p);
        }

        string? root = Path.GetPathRoot(p);
        if (root is not null && p.Length > root.Length)
        {
            p = p.TrimEnd('\\');
        }

        return p;
    }

    private static string ExpandShortName(string path)
    {
        var buffer = new StringBuilder(1024);
        uint length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        if (length > 0 && length < buffer.Capacity)
        {
            return buffer.ToString();
        }

        // O item final pode não existir: expande o pai e recoloca o nome.
        string? parent = Path.GetDirectoryName(path);
        if (parent is not null && !string.Equals(parent, path, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(ExpandShortName(parent), Path.GetFileName(path));
        }

        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);
}

/// <summary>Pastas do sistema usadas pela lista negra (injetáveis para testes).</summary>
public sealed record SystemLocations(
    string WindowsDirectory,
    string ProgramFiles,
    string ProgramFilesX86,
    string ProgramData,
    string UsersDirectory,
    string UserProfile,
    string LocalAppData,
    string RoamingAppData,
    string TempDirectory,
    string Desktop,
    string Documents,
    string Downloads,
    string Pictures,
    string Videos,
    string Music,
    IReadOnlyList<string> BrowserProfileRoots)
{
    public static SystemLocations FromEnvironment()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        string profile = F(Environment.SpecialFolder.UserProfile);
        string local = F(Environment.SpecialFolder.LocalApplicationData);
        string roaming = F(Environment.SpecialFolder.ApplicationData);
        string win = F(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(win))
        {
            win = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        }

        return new SystemLocations(
            win,
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(profile) ?? @"C:\Users",
            profile,
            local,
            roaming,
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "Downloads"),
            F(Environment.SpecialFolder.MyPictures),
            F(Environment.SpecialFolder.MyVideos),
            F(Environment.SpecialFolder.MyMusic),
            BrowserLocations.ProfileRoots(local, roaming));
    }
}

/// <summary>Raízes de dados de usuário dos navegadores suportados.</summary>
public static class BrowserLocations
{
    public static IReadOnlyList<string> ProfileRoots(string localAppData, string roamingAppData)
    {
        var roots = new List<string>
        {
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "Google", "Chrome Beta", "User Data"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"),
            Path.Combine(localAppData, "Vivaldi", "User Data"),
            Path.Combine(localAppData, "Chromium", "User Data"),
            Path.Combine(roamingAppData, "Opera Software", "Opera Stable"),
            Path.Combine(roamingAppData, "Opera Software", "Opera GX Stable"),
            Path.Combine(localAppData, "Opera Software", "Opera Stable"),
            Path.Combine(localAppData, "Opera Software", "Opera GX Stable"),
            Path.Combine(roamingAppData, "Mozilla", "Firefox", "Profiles"),
            Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles"),
        };

        // Arc (pacote da Microsoft Store: o nome da pasta inclui um sufixo do editor).
        string packages = Path.Combine(localAppData, "Packages");
        try
        {
            if (Directory.Exists(packages))
            {
                foreach (var dir in Directory.EnumerateDirectories(packages, "TheBrowserCompany.Arc*"))
                {
                    roots.Add(Path.Combine(dir, "LocalCache", "Local", "Arc", "User Data"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return roots;
    }
}
