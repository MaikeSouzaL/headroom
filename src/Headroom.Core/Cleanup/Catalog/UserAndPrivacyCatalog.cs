using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Settings;
using Headroom.Core.SystemTools;
using static Headroom.Core.Cleanup.Catalog.RuleBuilder;

namespace Headroom.Core.Cleanup.Catalog;

/// <summary>Arquivos do usuário (sempre "Revisar", nada marcado) e privacidade.</summary>
internal static class UserAndPrivacyCatalog
{
    private const CleanupCategory User = CleanupCategory.UserFiles;
    private const CleanupCategory Priv = CleanupCategory.Privacy;

    public static IEnumerable<ICleanupRule> CreateUserFiles(AppSettings settings)
    {
        yield return new LargeOldFilesRule();
        yield return new UserDuplicatesRule();
        yield return new SimilarImagesRule();
        yield return new ForgottenInstallersRule();
        yield return new ExtractedArchivesRule();
        yield return new EmptyFoldersRule();
        yield return new BrokenShortcutsRule();

        yield return Def("user.olddownloads", User, $"Downloads com mais de {settings.OldDownloadsDays} dias",
                "Arquivos da pasta Downloads sem modificação há mais de X dias (configurável).", RiskLevel.Review)
            .Contents("%DOWNLOADS%", minAgeDays: settings.OldDownloadsDays).Unselected().Off().Build();

        yield return Def("user.screenshots", User, $"Capturas de tela e gravações com mais de {settings.OldScreenshotsDays} dias",
                @"Capturas em Imagens\Screenshots e gravações em Vídeos\Captures antigas.", RiskLevel.Review)
            .Contents(@"%PICTURES%\Screenshots", minAgeDays: settings.OldScreenshotsDays)
            .Contents(@"%VIDEOS%\Captures", minAgeDays: settings.OldScreenshotsDays)
            .Contents(@"%USERPROFILE%\OneDrive\Pictures\Screenshots", minAgeDays: settings.OldScreenshotsDays)
            .Unselected().Off().Build();
    }

    public static IEnumerable<ICleanupRule> CreatePrivacy()
    {
        yield return Def("priv.recent", Priv, "Arquivos recentes", "Lista de arquivos abertos recentemente (atalhos em Recent). Os arquivos em si não são tocados.")
            .Contents(@"%APPDATA%\Microsoft\Windows\Recent", pattern: "*.lnk", recursive: false).Permanent().Off().Build();

        yield return Def("priv.jumplists", Priv, "Listas de atalhos (Jump Lists)", "Itens recentes/fixados nos menus da barra de tarefas.")
            .Contents(@"%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations")
            .Contents(@"%APPDATA%\Microsoft\Windows\Recent\CustomDestinations").Permanent().Off().Build();

        yield return new RegistryPrivacyRule("priv.runmru", "Histórico do Executar (Win+R)",
            "Comandos digitados na caixa Executar.", [@"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU"]);

        yield return new RegistryPrivacyRule("priv.explorer", "Histórico do Explorer",
            "Caminhos digitados na barra de endereços e pesquisas do Explorer.",
            [@"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths", @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery"]);

        yield return new PrivacyCommandRule("priv.clipboard", "Histórico da área de transferência",
            "Limpa o histórico da área de transferência (Win+V). Itens fixados permanecem.",
            new CommandStep("powershell",
                "[Windows.ApplicationModel.DataTransfer.Clipboard,Windows.ApplicationModel.DataTransfer,ContentType=WindowsRuntime] | Out-Null; " +
                "[Windows.ApplicationModel.DataTransfer.Clipboard]::ClearHistory() | Out-Null; 'OK'", PowerShell: true, TimeoutMinutes: 2));

        yield return new PrivacyCommandRule("priv.dns", "Cache de DNS",
            "Limpa o cache de resolução de nomes (ipconfig /flushdns). Útil quando sites não abrem após mudanças de DNS.",
            new CommandStep(CommandRunner.SystemTool("ipconfig.exe"), "/flushdns", TimeoutMinutes: 1));
    }
}
