using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Formatting;
using Headroom.Core.Settings;
using Headroom.Core.SystemTools;
using static Headroom.Core.Cleanup.Catalog.RuleBuilder;

namespace Headroom.Core.Cleanup.Catalog;

/// <summary>Limpezas do Windows (temporários, atualizações, logs, dumps, caches) e ações avançadas.</summary>
internal static class SystemCatalog
{
    private const CleanupCategory Sys = CleanupCategory.System;
    private const CleanupCategory Adv = CleanupCategory.Advanced;

    public static IEnumerable<ICleanupRule> Create(AppSettings settings)
    {
        yield return new RecycleBinRule();

        yield return Def("sys.temp.user", Sys, "Temporários do usuário (%TEMP%)",
                "Arquivos temporários do seu usuário com mais de 1 dia. Arquivos em uso são pulados.")
            .Contents("%TEMP%", minAgeDays: 1).Build();

        yield return Def("sys.temp.windows", Sys, @"Temporários do Windows (C:\Windows\Temp)",
                "Arquivos temporários do sistema com mais de 1 dia.")
            .Contents(@"%WINDIR%\Temp", minAgeDays: 1).Admin().Build();

        yield return Def("sys.temp.otherusers", Sys, "Temporários de outros usuários",
                "Pasta Temp dos outros perfis de usuário deste PC (arquivos com mais de 7 dias).", RiskLevel.Review)
            .Contents(@"%USERPROFILES%\AppData\Local\Temp", minAgeDays: 7).Admin().Off().Build();

        yield return Def("sys.wu.cache", Sys, "Cache do Windows Update",
                @"Atualizações já instaladas em SoftwareDistribution\Download. O serviço Windows Update é parado antes e reiniciado depois.")
            .Contents(@"%WINDIR%\SoftwareDistribution\Download").Services("wuauserv", "bits").Admin().Build();

        yield return Def("sys.deliveryopt", Sys, "Cache de Otimização de Entrega",
                "Arquivos de atualização compartilhados com outros PCs (Delivery Optimization).")
            .Contents(@"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")
            .Services("DoSvc").Admin().Build();

        yield return new TakeOwnershipPathRule(Def("sys.windowsold", Sys, "Instalação anterior do Windows (Windows.old)",
                "Cópia da versão anterior do Windows deixada após uma atualização grande. Depois de apagar NÃO é possível " +
                "voltar para a versão anterior. Excluída permanentemente (grande demais para a Lixeira).", RiskLevel.Review)
            .Self(@"%SYSTEMDRIVE%\Windows.old").Admin().Permanent().Off());

        yield return new TakeOwnershipPathRule(Def("sys.upgrade.leftovers", Sys, "Restos de atualização do Windows",
                @"Pastas temporárias de instalação/atualização: $Windows.~BT, $Windows.~WS, $GetCurrent e C:\ESD. Excluídas permanentemente.",
                RiskLevel.Review)
            .Self(@"%SYSTEMDRIVE%\$Windows.~BT").Self(@"%SYSTEMDRIVE%\$Windows.~WS").Self(@"%SYSTEMDRIVE%\$GetCurrent")
            .Self(@"%SYSTEMDRIVE%\ESD").Self(@"%SYSTEMDRIVE%\$WINDOWS.~Q").Self(@"%SYSTEMDRIVE%\Windows10Upgrade")
            .Admin().Permanent().Off());

        yield return Def("sys.wer", Sys, "Relatórios de erro do Windows",
                "Relatórios de falhas de programas (WER) já enviados ou na fila.")
            .Contents(@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive")
            .Contents(@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue")
            .Contents(@"%PROGRAMDATA%\Microsoft\Windows\WER\Temp")
            .Contents(@"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive")
            .Contents(@"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportQueue")
            .Contents(@"%LOCALAPPDATA%\Microsoft\Windows\WER\Temp").Build();

        yield return Def("sys.memorydump", Sys, "Dump de memória (MEMORY.DMP)",
                "Cópia da memória gravada depois de uma tela azul. Só é útil para diagnóstico técnico.")
            .Self(@"%WINDIR%\MEMORY.DMP").Admin().Permanent().Build();

        yield return Def("sys.minidump", Sys, "Minidumps de tela azul",
                "Pequenos arquivos de diagnóstico de telas azuis antigas.")
            .Contents(@"%WINDIR%\Minidump").Admin().Build();

        yield return Def("sys.livekernel", Sys, "LiveKernelReports",
                "Relatórios de travamentos de drivers (podem ter vários GB).")
            .Contents(@"%WINDIR%\LiveKernelReports", extensions: [".dmp"]).Admin().Build();

        yield return Def("sys.crashdumps", Sys, "Dumps de aplicativos (CrashDumps)",
                "Dumps de programas que travaram, gravados em AppData\\Local\\CrashDumps.")
            .Contents(@"%LOCALAPPDATA%\CrashDumps").Build();

        yield return Def("sys.logs", Sys, "Logs do sistema (CBS, DISM, Panther, instalação)",
                "Logs antigos (mais de 7 dias) do serviço de componentes, DISM, instalação e atualização do Windows.")
            .Contents(@"%WINDIR%\Logs\CBS", minAgeDays: 7, extensions: [".log", ".cab"])
            .Contents(@"%WINDIR%\Logs\DISM", minAgeDays: 7)
            .Contents(@"%WINDIR%\Logs\MoSetup", minAgeDays: 7)
            .Contents(@"%WINDIR%\Logs\WindowsUpdate", minAgeDays: 7)
            .Contents(@"%WINDIR%\Logs\SIH", minAgeDays: 7)
            .Contents(@"%WINDIR%\Logs\waasmedic", minAgeDays: 7)
            .Contents(@"%WINDIR%\Logs\NetSetup", minAgeDays: 7)
            .Contents(@"%WINDIR%\Panther", minAgeDays: 30)
            .Contents(@"%WINDIR%\debug", minAgeDays: 7, extensions: [".log"])
            .Contents("%WINDIR%", minAgeDays: 30, pattern: "*.log", recursive: false)
            .Contents(@"%WINDIR%\Performance\WinSAT\DataStore", minAgeDays: 30)
            .Admin().Build();

        yield return Def("sys.thumbcache", Sys, "Cache de miniaturas",
                "Miniaturas de fotos e vídeos do Explorer. O Windows recria quando precisar. Arquivos em uso pelo Explorer são pulados.")
            .Contents(@"%LOCALAPPDATA%\Microsoft\Windows\Explorer", pattern: "thumbcache_*.db", recursive: false).Build();

        yield return Def("sys.iconcache", Sys, "Cache de ícones",
                "Cache de ícones do Explorer (iconcache_*.db). Útil quando ícones aparecem errados.")
            .Contents(@"%LOCALAPPDATA%\Microsoft\Windows\Explorer", pattern: "iconcache_*.db", recursive: false)
            .Self(@"%LOCALAPPDATA%\IconCache.db").Build();

        yield return Def("sys.fontcache", Sys, "Cache de fontes",
                "Cache do serviço de fontes do Windows. Os serviços FontCache são parados e reiniciados.")
            .Contents(@"%WINDIR%\ServiceProfiles\LocalService\AppData\Local\FontCache")
            .Services("FontCache", "FontCache3.0.0.0").Admin().Off().Build();

        yield return Def("sys.inetcache", Sys, "Arquivos temporários da Internet (INetCache)",
                "Cache do mecanismo de navegação do Windows (IE/WebView antigo), páginas offline e programas baixados.")
            .Contents(@"%LOCALAPPDATA%\Microsoft\Windows\INetCache")
            .Contents(@"%WINDIR%\Downloaded Program Files")
            .Contents(@"%WINDIR%\Offline Web Pages").Build();

        yield return Def("sys.shader.directx", Sys, "Cache de shaders DirectX",
                "Shaders compilados pelo DirectX. São recriados nos jogos/apps (o primeiro carregamento pode ficar um pouco mais lento).")
            .Contents(@"%LOCALAPPDATA%\D3DSCache").Build();

        yield return Def("sys.shader.nvidia", Sys, "Cache de shaders NVIDIA",
                "DXCache, GLCache e NV_Cache da NVIDIA.")
            .Contents(@"%LOCALAPPDATA%\NVIDIA\DXCache").Contents(@"%LOCALAPPDATA%\NVIDIA\GLCache")
            .Contents(@"%LOCALAPPDATA%\NVIDIA Corporation\NV_Cache").Contents(@"%PROGRAMDATA%\NVIDIA Corporation\NV_Cache")
            .Detect(@"%LOCALAPPDATA%\NVIDIA", @"%LOCALAPPDATA%\NVIDIA Corporation", @"%PROGRAMDATA%\NVIDIA Corporation").Build();

        yield return Def("sys.shader.amd", Sys, "Cache de shaders AMD",
                "DxCache, DxcCache, GLCache e VkCache da AMD.")
            .Contents(@"%LOCALAPPDATA%\AMD\DxCache").Contents(@"%LOCALAPPDATA%\AMD\DxcCache")
            .Contents(@"%LOCALAPPDATA%\AMD\GLCache").Contents(@"%LOCALAPPDATA%\AMD\VkCache")
            .Detect(@"%LOCALAPPDATA%\AMD").Build();

        yield return Def("sys.driver.leftovers", Sys, "Restos de instaladores de drivers",
                @"Pastas extraídas por instaladores de driver (C:\NVIDIA, C:\AMD, C:\Intel) e downloads do NVIDIA App/GeForce Experience.",
                RiskLevel.Review)
            .Self(@"%SYSTEMDRIVE%\NVIDIA").Self(@"%SYSTEMDRIVE%\AMD").Self(@"%SYSTEMDRIVE%\Intel")
            .Contents(@"%PROGRAMDATA%\NVIDIA Corporation\Downloader")
            .Admin().Unselected().Build();

        yield return Def("sys.defender", Sys, "Histórico e backups do Windows Defender",
                "Histórico de verificações e backup de definições antigas. A proteção contra adulteração pode impedir parte da limpeza.",
                RiskLevel.Review)
            .Contents(@"%PROGRAMDATA%\Microsoft\Windows Defender\Scans\History\Results")
            .Contents(@"%PROGRAMDATA%\Microsoft\Windows Defender\Scans\History\Service\DetectionHistory")
            .Contents(@"%PROGRAMDATA%\Microsoft\Windows Defender\Definition Updates\Backup")
            .Admin().Off().Build();

        yield return Def("sys.feedbackhub", Sys, "Arquivos do Hub de Feedback",
                "Diagnósticos coletados pelo Hub de Feedback.")
            .Contents(@"%LOCALAPPDATA%\Packages\Microsoft.WindowsFeedbackHub_8wekyb3d8bbwe\LocalState\DiagOutputDir")
            .Contents(@"%LOCALAPPDATA%\Packages\Microsoft.WindowsFeedbackHub_8wekyb3d8bbwe\LocalCache").Build();

        yield return Def("sys.retaildemo", Sys, "Conteúdo de demonstração de varejo",
                "Vídeos e arquivos do modo de demonstração de lojas (Retail Demo).")
            .Contents(@"%PROGRAMDATA%\Microsoft\Windows\RetailDemo").Admin().Build();

        // ---------- Avançado (comandos do sistema) ----------
        string dism = CommandRunner.SystemTool("Dism.exe");

        yield return new CommandRule("adv.dism.cleanup", Adv, "Limpeza de componentes (DISM)",
            "Remove versões antigas de componentes do Windows substituídas por atualizações " +
            "(Dism /Online /Cleanup-Image /StartComponentCleanup). Pode levar vários minutos.", RiskLevel.Advanced)
        {
            RequiresAdmin = true,
            EnabledByDefault = false,
            Steps = [new CommandStep(dism, "/Online /Cleanup-Image /StartComponentCleanup /NoRestart", TimeoutMinutes: 120)],
            ActionText = "Executa DISM /StartComponentCleanup. O ganho só é conhecido ao final.",
        };

        yield return new CommandRule("adv.dism.resetbase", Adv, "Limpeza de componentes com ResetBase (DISM)",
            "Como a limpeza de componentes, mas também remove as versões substituídas de todas as atualizações. " +
            "ATENÇÃO: depois disso não é mais possível desinstalar as atualizações já instaladas.", RiskLevel.Advanced)
        {
            RequiresAdmin = true,
            EnabledByDefault = false,
            Steps = [new CommandStep(dism, "/Online /Cleanup-Image /StartComponentCleanup /ResetBase /NoRestart", TimeoutMinutes: 180)],
            ActionText = "Executa DISM /StartComponentCleanup /ResetBase (irreversível para desinstalar atualizações).",
        };

        yield return new ShadowCopiesRule();
        yield return new DriverStoreRule();

        string hiberfil = Path.Combine(DiskMeasure.SystemDrive, "hiberfil.sys");
        yield return new CommandRule("adv.hibernate.off", Adv, "Desativar hibernação",
            "Apaga o hiberfil.sys (do tamanho de boa parte da RAM) desativando a hibernação e a Inicialização Rápida " +
            "(powercfg /h off). Reative depois com \"powercfg /h on\".", RiskLevel.Advanced)
        {
            RequiresAdmin = true,
            EnabledByDefault = false,
            Steps = [new CommandStep(CommandRunner.SystemTool("powercfg.exe"), "/h off")],
            Estimator = (_, _) =>
            {
                long size = DiskMeasure.SafeFileLength(hiberfil);
                return Task.FromResult(size > 0 ? (size, (string?)null) : (-1L, (string?)"A hibernação já está desativada."));
            },
        };

        yield return new CommandRule("adv.hibernate.reduced", Adv, "Reduzir hibernação (só Inicialização Rápida)",
            "Mantém a Inicialização Rápida mas reduz o hiberfil.sys a cerca de 20% da RAM (powercfg /h /type reduced). " +
            "O PC deixa de poder hibernar por completo.", RiskLevel.Advanced)
        {
            RequiresAdmin = true,
            EnabledByDefault = false,
            Steps = [new CommandStep(CommandRunner.SystemTool("powercfg.exe"), "/h /type reduced")],
            Estimator = (_, _) =>
            {
                long size = DiskMeasure.SafeFileLength(hiberfil);
                return Task.FromResult(size > 0 ? (size / 2, (string?)"Estimativa: cerca de metade do hiberfil.sys atual.") : (-1L, (string?)"A hibernação está desativada."));
            },
        };

        yield return new CommandRule("adv.reservedstorage", Adv, "Desativar armazenamento reservado",
            "O Windows reserva cerca de 7 GB para atualizações. Desativar libera esse espaço, mas atualizações grandes podem " +
            "falhar por falta de espaço (DISM /Set-ReservedStorageState /State:Disabled).", RiskLevel.Advanced)
        {
            RequiresAdmin = true,
            EnabledByDefault = false,
            Steps = [new CommandStep(dism, "/Online /Set-ReservedStorageState /State:Disabled /NoRestart")],
            Estimator = (_, _) => Task.FromResult((7L * ByteSize.GB, (string?)"Estimativa típica; o valor real depende do Windows.")),
        };

        yield return new CommandRule("adv.compactos", Adv, "Compactar o Windows (CompactOS)",
            "Compacta os arquivos do sistema (compact /compactos:always), liberando de 2 a 4 GB com pouco impacto em PCs modernos. " +
            "Pode demorar de 10 a 30 minutos.", RiskLevel.Advanced)
        {
            RequiresAdmin = true,
            EnabledByDefault = false,
            Steps = [new CommandStep(CommandRunner.SystemTool("compact.exe"), "/compactos:always", TimeoutMinutes: 120)],
            Estimator = (_, _) => Task.FromResult((3L * ByteSize.GB, (string?)"Estimativa típica de 2 a 4 GB.")),
        };

        var folders = settings.CompressionFolders.Where(Directory.Exists).ToList();
        yield return new CommandRule("adv.ntfscompress", Adv, "Compressão NTFS de pastas escolhidas",
            "Compacta (sem apagar nada) as pastas escolhidas em Configurações, com compressão transparente do NTFS " +
            "(compact /c /s /i). Bom para pastas de documentos/projetos pouco usadas.", RiskLevel.Advanced)
        {
            RequiresAdmin = false,
            EnabledByDefault = false,
            Steps = folders.Select(f => new CommandStep(CommandRunner.SystemTool("compact.exe"), $"/c /s:\"{f}\" /i /q", TimeoutMinutes: 240)).ToList(),
            Estimator = (_, ct) => Task.FromResult(folders.Count == 0
                ? (-1L, (string?)"Nenhuma pasta escolhida: adicione pastas em Configurações > Compressão NTFS.")
                : (folders.Sum(f => DiskMeasure.DirectorySize(f, ct)) / 4, (string?)$"{folders.Count} pasta(s). Estimativa: ~25% do tamanho.")),
        };

        yield return new CommandRule("adv.eventlogs", Adv, "Limpar logs de eventos do Windows",
            "Apaga todos os registros do Visualizador de Eventos (wevtutil cl). Dificulta diagnosticar problemas passados.",
            RiskLevel.Advanced)
        {
            RequiresAdmin = true,
            EnabledByDefault = false,
            Steps = [new CommandStep("powershell", "wevtutil el | ForEach-Object { wevtutil cl \"$_\" 2>$null }; 'OK'", PowerShell: true, TimeoutMinutes: 20)],
            EstimatePaths = [@"%WINDIR%\System32\winevt\Logs"],
            ActionText = "Libera o espaço dos arquivos .evtx (que voltam a crescer com o uso).",
        };

        yield return new SearchIndexRule();
        yield return new PagefileInfoRule();
    }
}
