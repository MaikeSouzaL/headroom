using Headroom.Core.Cleanup.Rules;
using static Headroom.Core.Cleanup.Catalog.RuleBuilder;

namespace Headroom.Core.Cleanup.Catalog;

/// <summary>Aplicativos de comunicação, mídia, criação, escritório/nuvem e jogos.</summary>
internal static class AppCatalog
{
    private const CleanupCategory App = CleanupCategory.Applications;
    private const CleanupCategory Game = CleanupCategory.Games;

    private static readonly string[] ElectronCaches = ["Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", "DawnCache", "DawnWebGPUCache"];

    private static PathRuleDefinition ElectronCache(this PathRuleDefinition d, string root)
    {
        foreach (var folder in ElectronCaches)
        {
            d.Contents($@"{root}\{folder}");
        }

        return d;
    }

    public static IEnumerable<ICleanupRule> Create()
    {
        // ---------- Comunicação ----------
        yield return Def("app.discord", App, "Discord: cache", "Cache de imagens, código e GPU do Discord (Stable, PTB e Canary).")
            .ElectronCache(@"%APPDATA%\discord").ElectronCache(@"%APPDATA%\discordptb").ElectronCache(@"%APPDATA%\discordcanary")
            .Close("Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe")
            .Detect(@"%APPDATA%\discord", @"%APPDATA%\discordptb", @"%APPDATA%\discordcanary").Build();

        yield return Def("app.slack", App, "Slack: cache e logs", "Cache e logs do Slack (versão instalada e da Microsoft Store).")
            .ElectronCache(@"%APPDATA%\Slack").Contents(@"%APPDATA%\Slack\logs")
            .ElectronCache(@"%LOCALAPPDATA%\Packages\91750D7E.Slack_*\LocalCache\Roaming\Slack")
            .Close("slack.exe").Detect(@"%APPDATA%\Slack", @"%LOCALAPPDATA%\Packages\91750D7E.Slack_*").Build();

        yield return Def("app.teams.classic", App, "Microsoft Teams (clássico): cache",
                "Limpeza de cache recomendada pela Microsoft para o Teams clássico. Pode ser necessário entrar de novo.")
            .ElectronCache(@"%APPDATA%\Microsoft\Teams").Contents(@"%APPDATA%\Microsoft\Teams\blob_storage")
            .Contents(@"%APPDATA%\Microsoft\Teams\databases").Contents(@"%APPDATA%\Microsoft\Teams\IndexedDB")
            .Contents(@"%APPDATA%\Microsoft\Teams\Local Storage").Contents(@"%APPDATA%\Microsoft\Teams\tmp")
            .Contents(@"%APPDATA%\Microsoft\Teams\Application Cache\Cache")
            .CloseRequired("Teams.exe").Detect(@"%APPDATA%\Microsoft\Teams").Build();

        yield return Def("app.teams.new", App, "Microsoft Teams (novo): cache",
                "Cache do novo Teams (MSTeams). As configurações locais do Teams são redefinidas.", RiskLevel.Review)
            .Contents(@"%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams")
            .CloseRequired("ms-teams.exe").Detect(@"%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe").Build();

        yield return Def("app.zoom", App, "Zoom: logs", "Logs do Zoom.")
            .Contents(@"%APPDATA%\Zoom\logs").Contents(@"%APPDATA%\Zoom\data\Logs")
            .Close("Zoom.exe").Detect(@"%APPDATA%\Zoom").Build();

        yield return Def("app.whatsapp", App, "WhatsApp Desktop: cache", "Cache do navegador interno do WhatsApp Desktop (conversas e mídias não são apagadas).")
            .ElectronCache(@"%LOCALAPPDATA%\Packages\5319275A.WhatsAppDesktop_*\LocalState\EBWebView\*")
            .ElectronCache(@"%LOCALAPPDATA%\Packages\5319275A.WhatsAppDesktop_*\LocalCache\Roaming\WhatsApp")
            .Close("WhatsApp.exe").Detect(@"%LOCALAPPDATA%\Packages\5319275A.WhatsAppDesktop_*").Build();

        yield return Def("app.telegram", App, "Telegram Desktop: cache de mídia",
                "Cache de fotos, vídeos e figurinhas já vistos (são baixados de novo quando abertos).")
            .Contents(@"%APPDATA%\Telegram Desktop\tdata\user_data\cache").Contents(@"%APPDATA%\Telegram Desktop\tdata\user_data\media_cache")
            .Contents(@"%APPDATA%\Telegram Desktop\tdata\user_data#*\cache").Contents(@"%APPDATA%\Telegram Desktop\tdata\user_data#*\media_cache")
            .Close("Telegram.exe").Detect(@"%APPDATA%\Telegram Desktop").Build();

        // ---------- Mídia ----------
        yield return Def("app.spotify", App, "Spotify: cache de músicas",
                "Cache de streaming do Spotify. Músicas baixadas para ouvir offline podem precisar ser baixadas de novo.",
                RiskLevel.Review)
            .Contents(@"%LOCALAPPDATA%\Spotify\Storage").Contents(@"%LOCALAPPDATA%\Spotify\Data")
            .Contents(@"%LOCALAPPDATA%\Packages\SpotifyAB.SpotifyMusic_*\LocalCache\Spotify\Data")
            .CloseRequired("Spotify.exe").Detect(@"%LOCALAPPDATA%\Spotify", @"%LOCALAPPDATA%\Packages\SpotifyAB.SpotifyMusic_*").Build();

        yield return Def("app.vlc", App, "VLC: capas de álbuns", "Cache de capas (art) do VLC.")
            .Contents(@"%APPDATA%\vlc\art").Detect(@"%APPDATA%\vlc").Build();

        yield return Def("app.itunes.backups", App, "iTunes/Apple Devices: backups de iPhone/iPad",
                "Cada backup aparece separado com a data. Nada vem marcado: escolha os backups antigos que quer apagar.",
                RiskLevel.Review)
            .Children(@"%APPDATA%\Apple Computer\MobileSync\Backup").Children(@"%USERPROFILE%\Apple\MobileSync\Backup")
            .Unselected().Off().Build();

        yield return Def("app.itunes.ipsw", App, "iTunes: arquivos de atualização do iOS (.ipsw)",
                "Imagens de firmware do iPhone/iPad baixadas para atualizar ou restaurar (podem ser baixadas de novo).", RiskLevel.Review)
            .Contents(@"%APPDATA%\Apple Computer\iTunes", extensions: [".ipsw"])
            .Detect(@"%APPDATA%\Apple Computer\iTunes").Build();

        // ---------- Criação ----------
        yield return Def("app.adobe.mediacache", App, "Adobe: Media Cache (Premiere/After Effects)",
                "Arquivos de cache de mídia (áudio conformado, índices). São recriados ao abrir os projetos.")
            .Contents(@"%APPDATA%\Adobe\Common\Media Cache Files").Contents(@"%APPDATA%\Adobe\Common\Media Cache")
            .Contents(@"%APPDATA%\Adobe\Common\Peak Files")
            .Close("Adobe Premiere Pro.exe", "AfterFX.exe", "Adobe Media Encoder.exe").Detect(@"%APPDATA%\Adobe\Common").Build();

        yield return Def("app.adobe.cameraraw", App, "Adobe: cache do Camera Raw/Photoshop",
                "Cache de pré-visualização do Camera Raw e arquivos temporários do Photoshop.")
            .Contents(@"%APPDATA%\Adobe\CameraRaw\Cache").Contents("%TEMP%", pattern: "Photoshop Temp*", recursive: false)
            .Close("Photoshop.exe", "Lightroom.exe").Detect(@"%APPDATA%\Adobe\CameraRaw", @"%APPDATA%\Adobe\Adobe Photoshop*").Build();

        yield return Def("app.lightroom.previews", App, "Lightroom Classic: pré-visualizações",
                "Arquivos de prévia dos catálogos (*Previews.lrdata). São recriados, mas abrir as fotos fica mais lento até lá.",
                RiskLevel.Review)
            .Self(@"%PICTURES%\Lightroom\*Previews.lrdata").Self(@"%PICTURES%\Lightroom\*Smart Previews.lrdata")
            .CloseRequired("Lightroom.exe").Unselected().Build();

        yield return Def("app.resolve", App, "DaVinci Resolve: cache de render (CacheClip)",
                "Clipes de cache e otimizados do DaVinci Resolve (regenerados quando necessário).", RiskLevel.Review)
            .Contents(@"%APPDATA%\Blackmagic Design\DaVinci Resolve\Support\CacheClip")
            .Contents(@"%VIDEOS%\CacheClip").Contents(@"%USERPROFILE%\Movies\CacheClip")
            .CloseRequired("Resolve.exe").Build();

        yield return Def("app.obs", App, "OBS Studio: logs e relatórios de falha", "Logs, relatórios de falha e dados de profiler do OBS.")
            .Contents(@"%APPDATA%\obs-studio\logs").Contents(@"%APPDATA%\obs-studio\crashes").Contents(@"%APPDATA%\obs-studio\profiler_data")
            .Detect(@"%APPDATA%\obs-studio").Build();

        yield return Def("app.blender", App, "Blender: temporários", "Arquivos temporários e de recuperação antigos do Blender.")
            .Self(@"%TEMP%\blender_*", minAgeDays: 1).Self(@"%TEMP%\*.blend1", minAgeDays: 1)
            .Contents(@"%APPDATA%\Blender Foundation\Blender\*\cache")
            .Detect(@"%APPDATA%\Blender Foundation").Build();

        // ---------- Escritório e nuvem ----------
        yield return Def("app.office.cache", App, "Microsoft Office: cache de documentos",
                "Cache do Office (OfficeFileCache). Se houver uploads pendentes para o OneDrive/SharePoint, eles serão perdidos.",
                RiskLevel.Review)
            .Contents(@"%LOCALAPPDATA%\Microsoft\Office\16.0\OfficeFileCache").Contents(@"%LOCALAPPDATA%\Microsoft\Office\16.0\Wef")
            .CloseRequired("WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE", "OUTLOOK.EXE", "MSACCESS.EXE")
            .Detect(@"%LOCALAPPDATA%\Microsoft\Office\16.0").Off().Build();

        yield return Def("app.onedrive.logs", App, "OneDrive: logs", "Logs do cliente de sincronização do OneDrive.")
            .Contents(@"%LOCALAPPDATA%\Microsoft\OneDrive\logs").Contents(@"%LOCALAPPDATA%\Microsoft\OneDrive\setup\logs")
            .Detect(@"%LOCALAPPDATA%\Microsoft\OneDrive").Build();

        yield return Def("app.dropbox.cache", App, "Dropbox: .dropbox.cache", "Cache interno do Dropbox (versões antigas e arquivos apagados recentemente).")
            .Contents(@"%DROPBOX%\.dropbox.cache").Build();

        yield return Def("app.googledrive.cache", App, "Google Drive: cache de conteúdo",
                "Cópias em cache de arquivos do Google Drive para o computador. Arquivos ainda não sincronizados podem ser perdidos.",
                RiskLevel.Review)
            .Contents(@"%LOCALAPPDATA%\Google\DriveFS\*\content_cache")
            .CloseRequired("GoogleDriveFS.exe").Off().Build();

        yield return Def("app.java.cache", App, "Java: cache de implantação", "Cache de applets e aplicativos Java Web Start.")
            .Contents(@"%LOCALLOW%\Sun\Java\Deployment\cache").Build();

        yield return Def("app.acrobat.cache", App, "Adobe Acrobat/Reader: cache", "Cache do Acrobat e do Reader.")
            .Contents(@"%LOCALAPPDATA%\Adobe\AcroCef\DC\Acrobat\Cache").Contents(@"%LOCALAPPDATA%\Adobe\Acrobat\DC\Cache")
            .Contents(@"%LOCALLOW%\Adobe\Acrobat\DC\ConnectorIcons")
            .Close("Acrobat.exe", "AcroRd32.exe").Detect(@"%LOCALAPPDATA%\Adobe\AcroCef", @"%LOCALAPPDATA%\Adobe\Acrobat").Build();

        // ---------- Jogos ----------
        yield return Def("game.steam.shadercache", Game, "Steam: cache de shaders",
                "Shaders pré-compilados dos jogos da Steam (todas as bibliotecas). São recriados ao jogar.")
            .Contents(@"%STEAMLIBS%\steamapps\shadercache").Close("steam.exe").Build();

        yield return Def("game.steam.downloading", Game, "Steam: downloads temporários",
                "Downloads e atualizações incompletos (steamapps\\downloading e temp). Downloads em andamento recomeçam.", RiskLevel.Review)
            .Contents(@"%STEAMLIBS%\steamapps\downloading").Contents(@"%STEAMLIBS%\steamapps\temp")
            .CloseRequired("steam.exe").Build();

        yield return Def("game.steam.cache", Game, "Steam: depotcache, cache HTTP e logs", "Cache de depósitos, cache web, logs e dumps do cliente Steam.")
            .Contents(@"%STEAM%\depotcache").Contents(@"%STEAM%\appcache\httpcache").Contents(@"%STEAM%\logs").Contents(@"%STEAM%\dumps")
            .Close("steam.exe").Build();

        yield return Def("game.steam.redist", Game, "Steam: instaladores de redistribuíveis (_CommonRedist)",
                "Instaladores do DirectX/VC++/.NET que os jogos já instalados usaram. A Steam pode baixá-los de novo se precisar.",
                RiskLevel.Review)
            .Self(@"%STEAMLIBS%\steamapps\common\*\_CommonRedist").Unselected().Build();

        yield return Def("game.epic", Game, "Epic Games Launcher: cache e logs", "Cache web e logs do Epic Games Launcher.")
            .Contents(@"%LOCALAPPDATA%\EpicGamesLauncher\Saved\webcache*").Contents(@"%LOCALAPPDATA%\EpicGamesLauncher\Saved\Logs")
            .Close("EpicGamesLauncher.exe").Detect(@"%LOCALAPPDATA%\EpicGamesLauncher").Build();

        yield return Def("game.battlenet", Game, "Battle.net: cache e logs", "Cache e logs do Battle.net.")
            .Contents(@"%LOCALAPPDATA%\Battle.net\Cache").Contents(@"%LOCALAPPDATA%\Battle.net\BrowserCache")
            .Contents(@"%LOCALAPPDATA%\Blizzard Entertainment\Battle.net\Cache").Contents(@"%PROGRAMDATA%\Battle.net\Agent\Logs")
            .Close("Battle.net.exe").Detect(@"%LOCALAPPDATA%\Battle.net", @"%PROGRAMDATA%\Battle.net").Build();

        yield return Def("game.eaapp", Game, "EA App: cache e logs", "Cache do navegador interno e logs do EA App.")
            .Contents(@"%LOCALAPPDATA%\Electronic Arts\EA Desktop\CEF\BrowserCache").Contents(@"%LOCALAPPDATA%\Electronic Arts\EA Desktop\Logs")
            .Contents(@"%PROGRAMDATA%\EA Desktop\Logs")
            .Close("EADesktop.exe").Detect(@"%LOCALAPPDATA%\Electronic Arts\EA Desktop").Build();

        yield return Def("game.ubisoft", Game, "Ubisoft Connect: cache e logs", "Cache e logs do Ubisoft Connect.")
            .Contents(@"%LOCALAPPDATA%\Ubisoft Game Launcher\cache").Contents(@"%LOCALAPPDATA%\Ubisoft Game Launcher\logs")
            .Contents(@"%PROGRAMFILES(X86)%\Ubisoft\Ubisoft Game Launcher\cache").Contents(@"%PROGRAMFILES(X86)%\Ubisoft\Ubisoft Game Launcher\logs")
            .Close("upc.exe").Detect(@"%LOCALAPPDATA%\Ubisoft Game Launcher", @"%PROGRAMFILES(X86)%\Ubisoft\Ubisoft Game Launcher").Build();

        yield return Def("game.xbox", Game, "App Xbox: cache", "Cache do App Xbox e da Barra de Jogo.")
            .Contents(@"%LOCALAPPDATA%\Packages\Microsoft.GamingApp_8wekyb3d8bbwe\LocalCache")
            .Contents(@"%LOCALAPPDATA%\Packages\Microsoft.XboxGamingOverlay_8wekyb3d8bbwe\LocalCache")
            .Close("XboxPcApp.exe", "GameBar.exe")
            .Detect(@"%LOCALAPPDATA%\Packages\Microsoft.GamingApp_8wekyb3d8bbwe", @"%LOCALAPPDATA%\Packages\Microsoft.XboxGamingOverlay_8wekyb3d8bbwe").Build();

        yield return Def("game.gog", Game, "GOG Galaxy: cache e logs", "Cache web e logs do GOG Galaxy.")
            .Contents(@"%PROGRAMDATA%\GOG.com\Galaxy\webcache").Contents(@"%PROGRAMDATA%\GOG.com\Galaxy\logs")
            .Contents(@"%LOCALAPPDATA%\GOG.com\Galaxy\Applications\*\Storage\cache")
            .Close("GalaxyClient.exe").Detect(@"%PROGRAMDATA%\GOG.com\Galaxy", @"%LOCALAPPDATA%\GOG.com\Galaxy").Build();
    }
}
