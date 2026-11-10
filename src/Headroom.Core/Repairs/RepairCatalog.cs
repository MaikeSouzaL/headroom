using System.Text;
using Headroom.Core.Cleanup;
using Headroom.Core.Cleanup.Logging;
using Headroom.Core.SystemTools;

namespace Headroom.Core.Repairs;

/// <summary>Um passo de reparo.</summary>
/// <param name="File">Executável (ou "powershell" com <paramref name="PowerShell"/> = true, quando Arguments é o script).</param>
public sealed record RepairStep(string File, string Arguments, bool PowerShell = false, int TimeoutMinutes = 60,
    Encoding? Encoding = null, bool IgnoreExitCode = false, bool LaunchOnly = false);

/// <summary>Ação de reparo/manutenção do Windows ("Corrigir erros").</summary>
public sealed class RepairAction
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
    public bool RequiresAdmin { get; init; }
    public bool RequiresReboot { get; init; }

    /// <summary>Cria ponto de restauração antes (ações que mexem em componentes do sistema).</summary>
    public bool CreateRestorePoint { get; init; }

    /// <summary>Tempo típico, para o usuário saber o que esperar.</summary>
    public string? Duration { get; init; }

    public IReadOnlyList<RepairStep> Steps { get; init; } = Array.Empty<RepairStep>();

    /// <summary>Arquivo gerado para abrir ao final (ex.: relatório de bateria).</summary>
    public string? OpenAfter { get; init; }

    /// <summary>Só aparece quando a condição é verdadeira (ex.: notebook para relatório de bateria).</summary>
    public Func<bool>? IsAvailable { get; init; }
}

public sealed record RepairResult(bool Success, string Output, string Message, string? LogPath);

/// <summary>Catálogo de reparos e utilitários de manutenção, como os de grandes suítes de otimização.</summary>
public static class RepairCatalog
{
    private static string Sys(string exe) => CommandRunner.SystemTool(exe);

    private static readonly string Temp = Path.GetTempPath();

    public static IReadOnlyList<RepairAction> All { get; } = Build();

    public static RepairAction? Find(string id) => All.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<RepairAction> Build()
    {
        string systemDrive = DiskMeasure.SystemDrive.TrimEnd('\\');
        return
        [
            // ---------- Sistema ----------
            new()
            {
                Id = "repair.sfc", Category = "Arquivos do sistema", Name = "Verificar e reparar arquivos do sistema (SFC)",
                Description = "sfc /scannow: verifica todos os arquivos protegidos do Windows e substitui os corrompidos.",
                RequiresAdmin = true, Duration = "10 a 30 minutos",
                Steps = [new RepairStep(Sys("sfc.exe"), "/scannow", TimeoutMinutes: 90, Encoding: Encoding.Unicode, IgnoreExitCode: true)],
            },
            new()
            {
                Id = "repair.dism.check", Category = "Arquivos do sistema", Name = "Verificar a imagem do Windows (DISM CheckHealth/ScanHealth)",
                Description = "Verifica se o repositório de componentes do Windows está corrompido (não altera nada).",
                RequiresAdmin = true, Duration = "5 a 20 minutos",
                Steps =
                [
                    new RepairStep(Sys("Dism.exe"), "/Online /Cleanup-Image /CheckHealth", TimeoutMinutes: 30),
                    new RepairStep(Sys("Dism.exe"), "/Online /Cleanup-Image /ScanHealth", TimeoutMinutes: 60),
                ],
            },
            new()
            {
                Id = "repair.dism.restore", Category = "Arquivos do sistema", Name = "Reparar a imagem do Windows (DISM RestoreHealth)",
                Description = "Repara o repositório de componentes baixando arquivos bons do Windows Update. Rode antes do SFC quando o SFC não conseguir reparar.",
                RequiresAdmin = true, CreateRestorePoint = true, Duration = "10 a 40 minutos (precisa de Internet)",
                Steps = [new RepairStep(Sys("Dism.exe"), "/Online /Cleanup-Image /RestoreHealth", TimeoutMinutes: 120)],
            },

            // ---------- Disco ----------
            new()
            {
                Id = "repair.chkdsk", Category = "Disco", Name = $"Verificar o disco {systemDrive} (CHKDSK online)",
                Description = "chkdsk /scan: procura erros no sistema de arquivos sem desligar o disco. Se encontrar algo, agende o reparo completo.",
                RequiresAdmin = true, Duration = "5 a 30 minutos",
                Steps = [new RepairStep(Sys("chkdsk.exe"), $"{systemDrive} /scan", TimeoutMinutes: 120, IgnoreExitCode: true)],
            },
            new()
            {
                Id = "repair.chkdsk.boot", Category = "Disco", Name = $"Agendar reparo completo de {systemDrive} no próximo reinício",
                Description = "Marca o disco para o CHKDSK corrigir erros na próxima inicialização (fsutil dirty set). O próximo boot demora mais.",
                Risk = RiskLevel.Review, RequiresAdmin = true, RequiresReboot = true,
                Steps = [new RepairStep(Sys("fsutil.exe"), $"dirty set {systemDrive}")],
            },
            new()
            {
                Id = "repair.optimizedrives", Category = "Disco", Name = "Otimizar unidades (TRIM em SSD, desfragmentar HD)",
                Description = "defrag /C /O: executa a otimização certa para cada tipo de disco (TRIM em SSDs, desfragmentação em HDs). Também reativa o TRIM se estiver desligado.",
                RequiresAdmin = true, Duration = "1 minuto (SSD) a horas (HD fragmentado)",
                Steps =
                [
                    new RepairStep(Sys("fsutil.exe"), "behavior set DisableDeleteNotify 0", IgnoreExitCode: true),
                    new RepairStep(Sys("defrag.exe"), "/C /O /U", TimeoutMinutes: 600, IgnoreExitCode: true),
                ],
            },

            // ---------- Windows Update e Store ----------
            new()
            {
                Id = "repair.wureset", Category = "Windows Update", Name = "Redefinir o Windows Update",
                Description = "Para os serviços de atualização, renomeia SoftwareDistribution e catroot2 (cópias .old ficam guardadas) e reinicia os serviços. " +
                              "Resolve atualizações que falham sempre.",
                Risk = RiskLevel.Advanced, RequiresAdmin = true, CreateRestorePoint = true, Duration = "1 a 3 minutos",
                Steps =
                [
                    new RepairStep("powershell", """
                        $ErrorActionPreference = 'Continue'
                        $services = 'wuauserv','bits','cryptsvc','msiserver','usosvc'
                        foreach ($s in $services) { Stop-Service -Name $s -Force -ErrorAction SilentlyContinue }
                        $stamp = Get-Date -Format 'yyyyMMddHHmmss'
                        $sd = Join-Path $env:WINDIR 'SoftwareDistribution'
                        $cr = Join-Path $env:WINDIR 'System32\catroot2'
                        if (Test-Path $sd) { Rename-Item $sd "SoftwareDistribution.old_$stamp" -ErrorAction Continue; "Renomeado: $sd" }
                        if (Test-Path $cr) { Rename-Item $cr "catroot2.old_$stamp" -ErrorAction Continue; "Renomeado: $cr" }
                        foreach ($s in 'cryptsvc','bits','wuauserv','usosvc') { Start-Service -Name $s -ErrorAction SilentlyContinue }
                        'Windows Update redefinido.'
                        """, PowerShell: true, TimeoutMinutes: 10),
                ],
            },
            new()
            {
                Id = "repair.wsreset", Category = "Windows Update", Name = "Limpar o cache da Microsoft Store",
                Description = "wsreset: limpa o cache da Store sem apagar apps nem configurações (a Store abre ao final).",
                Steps = [new RepairStep(Sys("wsreset.exe"), string.Empty, TimeoutMinutes: 5, IgnoreExitCode: true)],
            },

            // ---------- Rede ----------
            new()
            {
                Id = "repair.dnsflush", Category = "Rede", Name = "Limpar o cache de DNS",
                Description = "ipconfig /flushdns: resolve sites que não abrem depois de mudanças de DNS.",
                Steps = [new RepairStep(Sys("ipconfig.exe"), "/flushdns", TimeoutMinutes: 1)],
            },
            new()
            {
                Id = "repair.iprenew", Category = "Rede", Name = "Renovar o endereço IP",
                Description = "ipconfig /release e /renew: pede um novo endereço ao roteador. A conexão cai por alguns segundos.",
                Risk = RiskLevel.Review,
                Steps =
                [
                    new RepairStep(Sys("ipconfig.exe"), "/release", TimeoutMinutes: 2, IgnoreExitCode: true),
                    new RepairStep(Sys("ipconfig.exe"), "/renew", TimeoutMinutes: 3, IgnoreExitCode: true),
                    new RepairStep(Sys("ipconfig.exe"), "/flushdns", TimeoutMinutes: 1),
                ],
            },
            new()
            {
                Id = "repair.networkreset", Category = "Rede", Name = "Redefinir rede (Winsock e TCP/IP)",
                Description = "netsh winsock reset e netsh int ip reset: corrige Internet que não funciona por pilha de rede corrompida (ex.: após remover VPN/antivírus). Exige reiniciar.",
                Risk = RiskLevel.Advanced, RequiresAdmin = true, RequiresReboot = true, CreateRestorePoint = true,
                Steps =
                [
                    new RepairStep(Sys("netsh.exe"), "winsock reset", TimeoutMinutes: 2),
                    new RepairStep(Sys("netsh.exe"), "int ip reset", TimeoutMinutes: 2, IgnoreExitCode: true),
                    new RepairStep(Sys("ipconfig.exe"), "/flushdns", TimeoutMinutes: 1),
                ],
            },

            // ---------- Aplicativos e Explorer ----------
            new()
            {
                Id = "repair.spooler", Category = "Impressão", Name = "Destravar a fila de impressão",
                Description = "Cancela os trabalhos presos na fila de todas as impressoras e reinicia o serviço de impressão (Spooler).",
                RequiresAdmin = true,
                Steps =
                [
                    new RepairStep("powershell", """
                        Get-Printer -ErrorAction SilentlyContinue | ForEach-Object { Get-PrintJob -PrinterName $_.Name -ErrorAction SilentlyContinue | Remove-PrintJob -ErrorAction SilentlyContinue }
                        Restart-Service -Name Spooler -Force -ErrorAction Stop
                        'Fila de impressão limpa e serviço reiniciado.'
                        """, PowerShell: true, TimeoutMinutes: 3),
                ],
            },
            new()
            {
                Id = "repair.iconcache", Category = "Explorer", Name = "Reconstruir cache de ícones e miniaturas",
                Description = "Reinicia o Explorer e apaga os caches de ícones/miniaturas. Corrige ícones em branco ou errados. A barra de tarefas some por alguns segundos.",
                Risk = RiskLevel.Review,
                Steps =
                [
                    new RepairStep("powershell", """
                        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
                        Start-Sleep -Seconds 2
                        $dir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
                        Get-ChildItem $dir -Filter 'iconcache_*.db' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
                        Get-ChildItem $dir -Filter 'thumbcache_*.db' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
                        Remove-Item (Join-Path $env:LOCALAPPDATA 'IconCache.db') -Force -ErrorAction SilentlyContinue
                        Start-Process explorer.exe
                        'Cache de ícones reconstruído.'
                        """, PowerShell: true, TimeoutMinutes: 2),
                ],
            },
            new()
            {
                Id = "repair.searchindex", Category = "Explorer", Name = "Reconstruir o índice de pesquisa",
                Description = "Recria o índice do Windows Search quando a pesquisa não encontra arquivos ou o índice fica enorme. Reindexar leva algumas horas.",
                Risk = RiskLevel.Review, RequiresAdmin = true,
                Steps =
                [
                    new RepairStep("powershell", """
                        Stop-Service WSearch -Force -ErrorAction SilentlyContinue
                        Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows Search' -Name 'SetupCompletedSuccessfully' -Value 0 -Type DWord
                        Start-Service WSearch
                        'Índice marcado para reconstrução.'
                        """, PowerShell: true, TimeoutMinutes: 3),
                ],
            },
            new()
            {
                Id = "repair.timesync", Category = "Sistema", Name = "Sincronizar o relógio do Windows",
                Description = "Força a sincronização da hora com o servidor de horário (w32tm). Corrige erros de certificado/HTTPS por relógio errado.",
                RequiresAdmin = true,
                Steps =
                [
                    new RepairStep("powershell", "Start-Service w32time -ErrorAction SilentlyContinue; w32tm /resync /force; 'OK'", PowerShell: true, TimeoutMinutes: 2, IgnoreExitCode: true),
                ],
            },

            // ---------- Diagnóstico e relatórios ----------
            new()
            {
                Id = "repair.memtest", Category = "Diagnóstico", Name = "Teste de memória RAM do Windows",
                Description = "Abre o Diagnóstico de Memória do Windows, que testa a RAM no próximo reinício (útil para telas azuis aleatórias).",
                Steps = [new RepairStep(Sys("mdsched.exe"), string.Empty, LaunchOnly: true)],
            },
            new()
            {
                Id = "repair.batteryreport", Category = "Diagnóstico", Name = "Relatório de bateria",
                Description = "powercfg /batteryreport: histórico de uso e capacidade da bateria (abre no navegador).",
                Steps = [new RepairStep(Sys("powercfg.exe"), $"/batteryreport /output \"{Path.Combine(Temp, "headroom-bateria.html")}\"", TimeoutMinutes: 2)],
                OpenAfter = Path.Combine(Temp, "headroom-bateria.html"),
                IsAvailable = HasBattery,
            },
            new()
            {
                Id = "repair.energyreport", Category = "Diagnóstico", Name = "Relatório de eficiência energética",
                Description = "powercfg /energy: observa o sistema por 60 segundos e lista problemas de energia (dispositivos que impedem economia, drivers).",
                RequiresAdmin = true, Duration = "1 minuto",
                Steps = [new RepairStep(Sys("powercfg.exe"), $"/energy /output \"{Path.Combine(Temp, "headroom-energia.html")}\" /duration 60", TimeoutMinutes: 3, IgnoreExitCode: true)],
                OpenAfter = Path.Combine(Temp, "headroom-energia.html"),
            },
            new()
            {
                Id = "repair.reliability", Category = "Diagnóstico", Name = "Monitor de Confiabilidade",
                Description = "Abre o histórico de falhas de apps e do Windows (perfmon /rel).",
                Steps = [new RepairStep(Sys("perfmon.exe"), "/rel", LaunchOnly: true)],
            },
        ];
    }

    private static bool HasBattery()
    {
        try
        {
            var status = new PowerStatus();
            return GetSystemPowerStatus(ref status) && status.BatteryFlag != 128 && status.BatteryFlag != 255;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Executa um reparo, transmitindo a saída linha a linha e gravando log.</summary>
    public static async Task<RepairResult> RunAsync(RepairAction action, bool isAdministrator, IProgress<string>? output,
        CancellationToken cancellationToken)
    {
        if (action.RequiresAdmin && !isAdministrator)
        {
            return new RepairResult(false, string.Empty, "Este reparo precisa de administrador. Use \"Reiniciar como administrador\".", null);
        }

        using var log = CleanupLog.Create("reparo");
        log.Section($"{action.Name} [{action.Id}]");
        var all = new StringBuilder();

        if (action.CreateRestorePoint && isAdministrator)
        {
            output?.Report("Criando ponto de restauração…");
            var (_, message) = await RestorePointService.CreateAsync($"Headroom - antes de: {action.Name}", cancellationToken).ConfigureAwait(false);
            output?.Report(message);
            log.Info(message);
        }

        bool success = true;
        string? failure = null;
        foreach (var step in action.Steps)
        {
            if (step.LaunchOnly)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(step.File, step.Arguments) { UseShellExecute = true });
                    output?.Report($"Aberto: {Path.GetFileName(step.File)}");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    success = false;
                    failure = ex.Message;
                }

                continue;
            }

            output?.Report(step.PowerShell ? "> PowerShell" : $"> {Path.GetFileName(step.File)} {step.Arguments}");
            var run = step.PowerShell
                ? await CommandRunner.RunPowerShellAsync(step.Arguments, TimeSpan.FromMinutes(step.TimeoutMinutes), output, cancellationToken).ConfigureAwait(false)
                : await CommandRunner.RunAsync(step.File, step.Arguments, TimeSpan.FromMinutes(step.TimeoutMinutes), output, step.Encoding, cancellationToken).ConfigureAwait(false);
            all.AppendLine(run.Combined);
            log.Info($"{step.File} {step.Arguments} → código {run.ExitCode}");
            foreach (var line in run.Combined.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                log.WriteRaw("    " + line.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(run.Error))
            {
                output?.Report(run.Error.Trim());
            }

            if (!run.Success && !step.IgnoreExitCode)
            {
                success = false;
                failure = run.TimedOut ? "Tempo limite excedido." : $"O comando terminou com código {run.ExitCode}.";
                break;
            }
        }

        if (success && action.OpenAfter is not null && File.Exists(action.OpenAfter))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(action.OpenAfter) { UseShellExecute = true });
        }

        string finalMessage = success
            ? "Concluído." + (action.RequiresReboot ? " Reinicie o PC para terminar." : string.Empty)
            : "Não concluído: " + failure;
        log.Info(finalMessage);
        return new RepairResult(success, all.ToString(), finalMessage, log.FilePath);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(ref PowerStatus status);
}
