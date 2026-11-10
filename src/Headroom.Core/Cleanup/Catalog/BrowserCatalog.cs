using Headroom.Core.Cleanup.Rules;
using static Headroom.Core.Cleanup.Catalog.RuleBuilder;

namespace Headroom.Core.Cleanup.Catalog;

/// <summary>
/// Navegadores baseados em Chromium (todos os perfis) e Firefox. O cache é seguro e vem marcado; histórico,
/// cookies, downloads, sessões e formulários são opcionais e desmarcados. Senhas, favoritos e extensões
/// nunca são tocados (garantido também pela lista negra do <see cref="Safety.ProtectedPaths"/>).
/// </summary>
internal static class BrowserCatalog
{
    private const CleanupCategory Br = CleanupCategory.Browsers;

    private sealed record Chromium(string Key, string Name, string UserData, string Process, string? CacheRoot = null, bool ProfileIsRoot = false);

    private static readonly Chromium[] ChromiumBrowsers =
    [
        new("chrome", "Google Chrome", @"%LOCALAPPDATA%\Google\Chrome\User Data", "chrome.exe"),
        new("edge", "Microsoft Edge", @"%LOCALAPPDATA%\Microsoft\Edge\User Data", "msedge.exe"),
        new("brave", "Brave", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data", "brave.exe"),
        new("vivaldi", "Vivaldi", @"%LOCALAPPDATA%\Vivaldi\User Data", "vivaldi.exe"),
        new("arc", "Arc", @"%LOCALAPPDATA%\Packages\TheBrowserCompany.Arc*\LocalCache\Local\Arc\User Data", "Arc.exe"),
        // Opera: o perfil é a própria pasta (Roaming) e o cache fica em Local.
        new("opera", "Opera", @"%APPDATA%\Opera Software\Opera Stable", "opera.exe", @"%LOCALAPPDATA%\Opera Software\Opera Stable", true),
        new("operagx", "Opera GX", @"%APPDATA%\Opera Software\Opera GX Stable", "opera.exe", @"%LOCALAPPDATA%\Opera Software\Opera GX Stable", true),
    ];

    private static readonly string[] ProfileCacheFolders =
    [
        "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache",
        "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "Media Cache",
    ];

    private static readonly string[] RootCacheFolders = ["ShaderCache", "GrShaderCache", "GraphiteDawnCache", "component_crx_cache"];

    public static IEnumerable<ICleanupRule> Create()
    {
        foreach (var b in ChromiumBrowsers)
        {
            // Perfis: "Default", "Profile 1", "Guest Profile"... (curinga * em User Data).
            string profiles = b.ProfileIsRoot ? b.UserData : b.UserData + @"\*";

            var cache = Def($"browser.{b.Key}.cache", Br, $"{b.Name}: cache",
                    "Cache de páginas, código e GPU de todos os perfis. É recriado ao navegar. Com o navegador aberto, arquivos em uso são pulados.")
                .Close(b.Process).Detect(b.UserData);
            foreach (var folder in ProfileCacheFolders)
            {
                cache.Contents($@"{profiles}\{folder}");
                if (b.CacheRoot is not null)
                {
                    cache.Contents($@"{b.CacheRoot}\{folder}");
                }
            }

            if (!b.ProfileIsRoot)
            {
                foreach (var folder in RootCacheFolders)
                {
                    cache.Contents($@"{b.UserData}\{folder}");
                }
            }

            yield return cache.Build();

            yield return Def($"browser.{b.Key}.history", Br, $"{b.Name}: histórico de navegação",
                    "Histórico, sites mais visitados e sugestões da barra de endereços (também apaga a lista de downloads). Favoritos e senhas ficam.",
                    RiskLevel.Review)
                .Self($@"{profiles}\History").Self($@"{profiles}\History-journal").Self($@"{profiles}\Visited Links")
                .Self($@"{profiles}\Top Sites").Self($@"{profiles}\Top Sites-journal").Self($@"{profiles}\Shortcuts")
                .Self($@"{profiles}\Shortcuts-journal").Self($@"{profiles}\Network Action Predictor")
                .CloseRequired(b.Process).Detect(b.UserData).Privacy().Permanent().Off().Build();

            yield return Def($"browser.{b.Key}.cookies", Br, $"{b.Name}: cookies",
                    "Cookies de todos os sites (você precisará fazer login de novo nos sites).", RiskLevel.Review)
                .Self($@"{profiles}\Network\Cookies").Self($@"{profiles}\Network\Cookies-journal")
                .Self($@"{profiles}\Cookies").Self($@"{profiles}\Cookies-journal")
                .CloseRequired(b.Process).Detect(b.UserData).Privacy().Permanent().Off().Build();

            yield return Def($"browser.{b.Key}.sessions", Br, $"{b.Name}: sessões e abas salvas",
                    "Abas e janelas salvas para restaurar a sessão anterior.", RiskLevel.Review)
                .Self($@"{profiles}\Sessions").Self($@"{profiles}\Current Session").Self($@"{profiles}\Last Session")
                .Self($@"{profiles}\Current Tabs").Self($@"{profiles}\Last Tabs")
                .CloseRequired(b.Process).Detect(b.UserData).Privacy().Permanent().Off().Build();

            yield return new BrowserSqlRule($"browser.{b.Key}.downloads", $"{b.Name}: histórico de downloads",
                "Apaga só a lista de downloads (os arquivos baixados não são tocados).",
                [$@"{profiles}\History"], ["DELETE FROM downloads_url_chains;", "DELETE FROM downloads_slices;", "DELETE FROM downloads;"],
                b.Process);

            yield return new BrowserSqlRule($"browser.{b.Key}.forms", $"{b.Name}: dados de formulários",
                "Apaga o preenchimento automático de campos de formulário. Endereços, cartões e senhas salvos não são apagados.",
                [$@"{profiles}\Web Data"], ["DELETE FROM autofill;", "DELETE FROM autofill_sync_metadata;"],
                b.Process);
        }

        // ---------- Firefox ----------
        const string roaming = @"%APPDATA%\Mozilla\Firefox\Profiles\*";
        const string local = @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*";
        const string detect = @"%APPDATA%\Mozilla\Firefox\Profiles";

        yield return Def("browser.firefox.cache", Br, "Mozilla Firefox: cache",
                "Cache de páginas, inicialização, miniaturas e shaders de todos os perfis.")
            .Contents($@"{local}\cache2").Contents($@"{local}\startupCache").Contents($@"{local}\thumbnails")
            .Contents($@"{local}\jumpListCache").Contents($@"{local}\shader-cache").Contents($@"{roaming}\shader-cache")
            .Close("firefox.exe").Detect(detect).Build();

        yield return new BrowserSqlRule("browser.firefox.history", "Mozilla Firefox: histórico de navegação",
            "Apaga visitas e sugestões, mantendo os favoritos (que ficam no mesmo banco places.sqlite).",
            [$@"{roaming}\places.sqlite"],
            [
                "DELETE FROM moz_historyvisits;",
                "DELETE FROM moz_inputhistory;",
                "DELETE FROM moz_places WHERE foreign_count = 0 AND id NOT IN (SELECT place_id FROM moz_annos);",
            ],
            "firefox.exe");

        yield return new BrowserSqlRule("browser.firefox.downloads", "Mozilla Firefox: histórico de downloads",
            "Apaga só a lista de downloads (os arquivos baixados não são tocados).",
            [$@"{roaming}\places.sqlite"],
            ["DELETE FROM moz_annos WHERE anno_attribute_id IN (SELECT id FROM moz_anno_attributes WHERE name LIKE 'downloads/%');"],
            "firefox.exe");

        yield return Def("browser.firefox.cookies", Br, "Mozilla Firefox: cookies",
                "Cookies de todos os sites (você precisará fazer login de novo).", RiskLevel.Review)
            .Self($@"{roaming}\cookies.sqlite").Self($@"{roaming}\cookies.sqlite-wal").Self($@"{roaming}\cookies.sqlite-shm")
            .CloseRequired("firefox.exe").Detect(detect).Privacy().Permanent().Off().Build();

        yield return Def("browser.firefox.sessions", Br, "Mozilla Firefox: sessões e abas salvas",
                "Arquivos de restauração de sessão.", RiskLevel.Review)
            .Self($@"{roaming}\sessionstore.jsonlz4").Self($@"{roaming}\sessionstore-backups")
            .CloseRequired("firefox.exe").Detect(detect).Privacy().Permanent().Off().Build();

        yield return Def("browser.firefox.forms", Br, "Mozilla Firefox: dados de formulários",
                "Histórico de preenchimento de formulários (formhistory.sqlite).", RiskLevel.Review)
            .Self($@"{roaming}\formhistory.sqlite").Self($@"{roaming}\formhistory.sqlite-wal")
            .CloseRequired("firefox.exe").Detect(detect).Privacy().Permanent().Off().Build();
    }
}
