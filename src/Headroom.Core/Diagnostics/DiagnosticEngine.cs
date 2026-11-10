using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Drives;
using Headroom.Core.Formatting;
using Headroom.Core.Optimization;
using Headroom.Core.SystemTools;
using Microsoft.Win32;

namespace Headroom.Core.Diagnostics;

/// <summary>
/// Diagnóstico de saúde do PC (somente leitura): discos (SMART, TRIM, espaço, erros NTFS), memória,
/// CPU, inicialização, reinício pendente, Windows Update, drivers com problema, erros críticos no log
/// de eventos, segurança (antivírus, firewall, UAC), energia, bateria e rede. Cada achado traz uma
/// recomendação e, quando faz sentido, um reparo ou página sugeridos.
/// </summary>
public sealed class DiagnosticEngine
{
    public bool CheckNetwork { get; init; } = true;

    public async Task<DiagnosticReport> RunAsync(bool isAdministrator, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var findings = new List<DiagnosticFinding>();

        progress?.Report("Consultando o Windows (discos, drivers, segurança, eventos)…");
        var wmiTask = QueryWmiAsync(cancellationToken);
        progress?.Report("Medindo CPU e memória…");
        var cpuTask = SystemInfo.MeasureCpuAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var processesTask = SystemInfo.TopProcessesAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var startupTask = StartupManager.ListAsync(true, cancellationToken);
        var trimTask = QueryTrimAsync(cancellationToken);
        var planTask = SystemInfo.GetPowerPlansAsync(cancellationToken);
        var networkTask = CheckNetwork ? CheckNetworkAsync(cancellationToken) : Task.FromResult<DiagnosticFinding?>(null);

        findings.AddRange(CheckFreeSpace());
        findings.Add(CheckSystemVersion());
        findings.Add(CheckUptime());
        findings.Add(CheckPendingReboot());
        findings.Add(CheckUac());
        findings.Add(CheckTempFolder());

        double cpu = await cpuTask.ConfigureAwait(false);
        var processes = await processesTask.ConfigureAwait(false);
        findings.Add(CheckMemory(processes));
        findings.Add(CheckCpu(cpu, processes));

        progress?.Report("Verificando programas de inicialização…");
        findings.Add(CheckStartup(await startupTask.ConfigureAwait(false)));
        findings.Add(CheckTrim(await trimTask.ConfigureAwait(false)));
        findings.Add(CheckPowerPlan(await planTask.ConfigureAwait(false)));

        progress?.Report("Analisando discos, drivers e eventos…");
        var wmi = await wmiTask.ConfigureAwait(false);
        findings.AddRange(InterpretWmi(wmi, isAdministrator));

        progress?.Report("Testando a conexão…");
        if (await networkTask.ConfigureAwait(false) is { } network)
        {
            findings.Add(network);
        }

        stopwatch.Stop();
        var ordered = findings
            .OrderByDescending(f => f.Status)
            .ThenBy(f => f.Category, StringComparer.CurrentCulture)
            .ToList();
        return new DiagnosticReport(ordered, stopwatch.Elapsed);
    }

    // ---------------------------------------------------------------- verificações locais

    private static IEnumerable<DiagnosticFinding> CheckFreeSpace()
    {
        foreach (var drive in DriveService.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            double free = drive.FreeFraction;
            var status = free < 0.10 ? HealthStatus.Problem : free < 0.20 ? HealthStatus.Attention : HealthStatus.Ok;
            yield return new DiagnosticFinding("Disco", $"Espaço livre em {drive.Letter}", status,
                $"{ByteSize.Format(drive.FreeBytes)} livres de {ByteSize.Format(drive.TotalBytes)} ({ByteSize.FormatPercent(free)}) — {drive.MediaKindText}.",
                status == HealthStatus.Ok ? null : "Pouco espaço deixa o Windows lento e impede atualizações. Use a Limpeza e o Mapa de espaço.",
                ActionId: status == HealthStatus.Ok ? null : "page:Cleanup", ActionText: "Abrir a Limpeza");
        }
    }

    private static DiagnosticFinding CheckSystemVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName")?.ToString() ?? "Windows";
            string display = key?.GetValue("DisplayVersion")?.ToString() ?? key?.GetValue("ReleaseId")?.ToString() ?? string.Empty;
            string build = key?.GetValue("CurrentBuild")?.ToString() ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
            string ubr = key?.GetValue("UBR")?.ToString() ?? "0";
            int buildNumber = int.TryParse(build, out var b) ? b : 0;
            if (buildNumber >= 22000 && product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            bool win10 = buildNumber < 22000;
            return new DiagnosticFinding("Sistema", "Versão do Windows", win10 ? HealthStatus.Info : HealthStatus.Ok,
                $"{product} {display} (compilação {build}.{ubr}).",
                win10 ? "O Windows 10 deixou de receber atualizações gratuitas em outubro de 2025; considere atualizar para o Windows 11 ou aderir ao ESU." : null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return new DiagnosticFinding("Sistema", "Versão do Windows", HealthStatus.Info, Environment.OSVersion.VersionString);
        }
    }

    private static DiagnosticFinding CheckUptime()
    {
        var uptime = SystemInfo.Uptime;
        var status = uptime.TotalDays > 14 ? HealthStatus.Attention : HealthStatus.Ok;
        return new DiagnosticFinding("Sistema", "Tempo ligado", status,
            $"O PC está ligado há {(int)uptime.TotalDays} dia(s) e {uptime.Hours} hora(s) sem reiniciar.",
            status == HealthStatus.Ok ? null : "Reiniciar de vez em quando libera memória e conclui atualizações pendentes " +
                                               "(com a Inicialização Rápida, \"Desligar\" não conta como reinício).");
    }

    private static DiagnosticFinding CheckPendingReboot()
    {
        var reasons = new List<string>();
        try
        {
            if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is { } k1)
            {
                k1.Dispose();
                reasons.Add("Componentes do Windows aguardando reinício");
            }

            if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is { } k2)
            {
                k2.Dispose();
                reasons.Add("Windows Update aguardando reinício");
            }

            using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
            {
                reasons.Add("Arquivos aguardando substituição no próximo reinício");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        return reasons.Count == 0
            ? new DiagnosticFinding("Sistema", "Reinício pendente", HealthStatus.Ok, "Nenhum reinício pendente.")
            : new DiagnosticFinding("Sistema", "Reinício pendente", HealthStatus.Attention, "O Windows precisa reiniciar para concluir alterações.",
                "Reinicie o PC quando puder.", reasons);
    }

    private static DiagnosticFinding CheckUac()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            bool enabled = Convert.ToInt32(key?.GetValue("EnableLUA") ?? 1, CultureInfo.InvariantCulture) != 0;
            return enabled
                ? new DiagnosticFinding("Segurança", "Controle de Conta de Usuário (UAC)", HealthStatus.Ok, "O UAC está ativado.")
                : new DiagnosticFinding("Segurança", "Controle de Conta de Usuário (UAC)", HealthStatus.Problem,
                    "O UAC está desativado: qualquer programa roda com privilégios totais.",
                    "Ative o UAC em Painel de Controle > Contas de Usuário > Alterar configurações de Controle de Conta de Usuário.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or FormatException or InvalidCastException)
        {
            return new DiagnosticFinding("Segurança", "Controle de Conta de Usuário (UAC)", HealthStatus.Info, "Não foi possível ler a configuração do UAC.");
        }
    }

    private static DiagnosticFinding CheckTempFolder()
    {
        long size = DiskMeasure.DirectorySize(Path.GetTempPath());
        var status = size > 5L * ByteSize.GB ? HealthStatus.Attention : HealthStatus.Ok;
        return new DiagnosticFinding("Disco", "Arquivos temporários", status,
            $"A pasta temporária do usuário ocupa {ByteSize.Format(size)}.",
            status == HealthStatus.Ok ? null : "Limpe os temporários na tela de Limpeza (perfil Rápida).",
            ActionId: status == HealthStatus.Ok ? null : "page:Cleanup", ActionText: "Abrir a Limpeza");
    }

    private static DiagnosticFinding CheckMemory(List<ProcessUsage> processes)
    {
        var memory = SystemInfo.GetMemory();
        var top = processes.GroupBy(p => p.Name)
            .Select(g => (Name: g.Key, Bytes: g.Sum(p => p.WorkingSet), Count: g.Count()))
            .OrderByDescending(x => x.Bytes).Take(8)
            .Select(x => $"{x.Name}{(x.Count > 1 ? $" ({x.Count} processos)" : string.Empty)}: {ByteSize.Format(x.Bytes)}")
            .ToList();
        var status = memory.UsedFraction > 0.90 ? HealthStatus.Problem : memory.UsedFraction > 0.80 ? HealthStatus.Attention : HealthStatus.Ok;
        string advice = memory.TotalBytes < 8L * ByteSize.GB
            ? "Com menos de 8 GB de RAM, fechar programas pesados e reduzir a inicialização faz bastante diferença."
            : "Feche programas que não está usando ou reduza o que inicia com o Windows.";
        return new DiagnosticFinding("Desempenho", "Memória (RAM)", status,
            $"{ByteSize.Format(memory.UsedBytes)} em uso de {ByteSize.Format(memory.TotalBytes)} ({ByteSize.FormatPercent(memory.UsedFraction)}).",
            status == HealthStatus.Ok ? null : advice, top, status == HealthStatus.Ok ? null : "page:Processes", "Ver processos");
    }

    private static DiagnosticFinding CheckCpu(double cpu, List<ProcessUsage> processes)
    {
        var top = processes.Where(p => p.CpuPercent > 0.01).OrderByDescending(p => p.CpuPercent).Take(6)
            .Select(p => $"{p.Name}: {ByteSize.FormatPercent(p.CpuPercent)}").ToList();
        var status = cpu > 0.85 ? HealthStatus.Attention : HealthStatus.Ok;
        return new DiagnosticFinding("Desempenho", "Processador (CPU)", status,
            $"Uso atual de {ByteSize.FormatPercent(cpu)} em {Environment.ProcessorCount} núcleos lógicos.",
            status == HealthStatus.Ok ? null : "Algum programa está consumindo muito processador agora. Veja a lista de processos.",
            top, status == HealthStatus.Ok ? null : "page:Processes", "Ver processos");
    }

    private static DiagnosticFinding CheckStartup(List<StartupItem> items)
    {
        var enabled = items.Where(i => i.Enabled).ToList();
        var broken = items.Where(i => i.Enabled && i.ExecutablePath is not null && !i.ExecutableExists &&
                                      Path.IsPathFullyQualified(i.ExecutablePath)).ToList();
        var status = broken.Count > 0 || enabled.Count > 20 ? HealthStatus.Attention : HealthStatus.Ok;
        var details = enabled.Select(i => $"{i.Name} — {i.Publisher ?? "editor desconhecido"}").Take(30).ToList();
        details.AddRange(broken.Select(b => $"Programa não existe mais: {b.Name} ({b.ExecutablePath})"));
        return new DiagnosticFinding("Desempenho", "Programas na inicialização", status,
            $"{enabled.Count} programa(s) iniciam com o Windows ({items.Count - enabled.Count} desativado(s))." +
            (broken.Count > 0 ? $" {broken.Count} apontam para programas que não existem mais." : string.Empty),
            status == HealthStatus.Ok ? "Desative o que não precisa abrir sozinho para o Windows iniciar mais rápido."
                : "Muitos itens deixam a inicialização lenta. Desative os desnecessários (é reversível).",
            details, "page:Startup", "Gerenciar inicialização");
    }

    private static async Task<bool?> QueryTrimAsync(CancellationToken ct)
    {
        var run = await CommandRunner.RunAsync(CommandRunner.SystemTool("fsutil.exe"), "behavior query DisableDeleteNotify",
            TimeSpan.FromSeconds(15), cancellationToken: ct).ConfigureAwait(false);
        // Saída (localizada): "NTFS DisableDeleteNotify = 0 (...)". 0 = TRIM ativado.
        var line = run.Output.Split('\n').FirstOrDefault(l => l.Contains("NTFS", StringComparison.OrdinalIgnoreCase) && l.Contains('='));
        if (line is null)
        {
            return null;
        }

        string value = line[(line.IndexOf('=') + 1)..].Trim();
        return value.StartsWith('0') ? true : value.StartsWith('1') ? false : null;
    }

    private static DiagnosticFinding CheckTrim(bool? trimEnabled)
    {
        bool hasSsd = DriveService.GetDrives().Any(d => d.MediaKind == DriveMediaKind.Ssd);
        return trimEnabled switch
        {
            true => new DiagnosticFinding("Disco", "TRIM para SSD", HealthStatus.Ok, "O TRIM está ativado (mantém o desempenho dos SSDs)."),
            false when hasSsd => new DiagnosticFinding("Disco", "TRIM para SSD", HealthStatus.Attention,
                "O TRIM está desativado e há SSD neste PC.", "Ative com \"fsutil behavior set DisableDeleteNotify 0\" e otimize as unidades.",
                ActionId: "repair.optimizedrives", ActionText: "Otimizar unidades"),
            false => new DiagnosticFinding("Disco", "TRIM para SSD", HealthStatus.Info, "O TRIM está desativado (sem SSD detectado)."),
            _ => new DiagnosticFinding("Disco", "TRIM para SSD", HealthStatus.Info, "Não foi possível consultar o TRIM."),
        };
    }

    private static DiagnosticFinding CheckPowerPlan(List<PowerPlan> plans)
    {
        var active = plans.FirstOrDefault(p => p.IsActive);
        bool hasBattery = SystemInformationHasBattery();
        if (active is null)
        {
            return new DiagnosticFinding("Energia", "Plano de energia", HealthStatus.Info, "Não foi possível ler o plano de energia.");
        }

        bool powerSaver = active.Id == new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        return new DiagnosticFinding("Energia", "Plano de energia", powerSaver && !hasBattery ? HealthStatus.Attention : HealthStatus.Ok,
            $"Plano ativo: {active.Name}.",
            powerSaver && !hasBattery ? "O plano \"Economia de energia\" reduz o desempenho em um desktop. Use \"Equilibrado\" ou \"Alto desempenho\"." : null,
            plans.Select(p => (p.IsActive ? "● " : "○ ") + p.Name).ToList(), "page:Startup", "Alterar plano");
    }

    private static bool SystemInformationHasBattery()
    {
        try
        {
            var status = new SystemPowerStatus();
            return GetSystemPowerStatus(ref status) && status.BatteryFlag != 128 && status.BatteryFlag != 255;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static async Task<DiagnosticFinding?> CheckNetworkAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync("www.msftconnecttest.com", ct).ConfigureAwait(false);
            long dnsMs = stopwatch.ElapsedMilliseconds;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            stopwatch.Restart();
            string body = await http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt", ct).ConfigureAwait(false);
            long httpMs = stopwatch.ElapsedMilliseconds;
            bool ok = body.Contains("Microsoft Connect Test", StringComparison.OrdinalIgnoreCase);
            var status = !ok ? HealthStatus.Attention : dnsMs > 800 ? HealthStatus.Attention : HealthStatus.Ok;
            return new DiagnosticFinding("Rede", "Conexão com a Internet", status,
                ok ? $"Conectado. DNS em {dnsMs} ms, resposta em {httpMs} ms." : "A conexão responde, mas parece passar por um portal ou proxy.",
                status == HealthStatus.Ok ? null : "DNS lento ou portal de login. Experimente limpar o cache DNS ou redefinir a rede.",
                ActionId: status == HealthStatus.Ok ? null : "repair.dnsflush", ActionText: "Limpar cache DNS");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException)
        {
            return new DiagnosticFinding("Rede", "Conexão com a Internet", HealthStatus.Attention,
                "Sem acesso à Internet (ou bloqueado por firewall/proxy).",
                "Verifique o cabo/Wi-Fi. Se só este PC está sem Internet, tente \"Redefinir rede\".",
                ActionId: "repair.networkreset", ActionText: "Redefinir rede");
        }
    }

    // ---------------------------------------------------------------- consultas WMI em lote

    private const string WmiScript = """
        $r = @{}
        try { $r.Disks = @(Get-PhysicalDisk -ErrorAction Stop | ForEach-Object { $rel = $null; try { $rel = $_ | Get-StorageReliabilityCounter -ErrorAction Stop } catch {}; [pscustomobject]@{ Name=$_.FriendlyName; Media="$($_.MediaType)"; Bus="$($_.BusType)"; Health="$($_.HealthStatus)"; Status="$($_.OperationalStatus)"; Size=$_.Size; Temp=$rel.Temperature; Wear=$rel.Wear; ReadErr=$rel.ReadErrorsUncorrected; WriteErr=$rel.WriteErrorsUncorrected; Hours=$rel.PowerOnHours } }) } catch {}
        try { $r.Volumes = @(Get-CimInstance Win32_Volume -Filter "DriveType=3" -ErrorAction Stop | Where-Object DriveLetter | Select-Object DriveLetter, DirtyBitSet, FileSystem) } catch {}
        try { $r.Devices = @(Get-CimInstance Win32_PnPEntity -Filter "ConfigManagerErrorCode<>0" -ErrorAction Stop | Select-Object Name, ConfigManagerErrorCode, PNPClass) } catch {}
        try { $h = Get-HotFix -ErrorAction Stop | Where-Object InstalledOn | Sort-Object InstalledOn -Descending | Select-Object -First 1; if ($h) { $r.LastHotfix = @{ Id=$h.HotFixID; Date=$h.InstalledOn.ToString('o') } } } catch {}
        try { $m = Get-MpComputerStatus -ErrorAction Stop; $r.Defender = @{ Av=$m.AntivirusEnabled; Rt=$m.RealTimeProtectionEnabled; SigAge=$m.AntivirusSignatureAge; QuickAge=$m.QuickScanAge } } catch {}
        try { $r.AvProducts = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntivirusProduct -ErrorAction Stop | Select-Object displayName, productState) } catch {}
        try { $r.Firewall = @(Get-NetFirewallProfile -ErrorAction Stop | Select-Object Name, Enabled) } catch {}
        try { $d = Get-CimInstance -Namespace root/wmi -ClassName BatteryStaticData -ErrorAction Stop | Select-Object -First 1; $f = Get-CimInstance -Namespace root/wmi -ClassName BatteryFullChargedCapacity -ErrorAction Stop | Select-Object -First 1; if ($d -and $f) { $r.Battery = @{ Design=$d.DesignedCapacity; Full=$f.FullChargedCapacity } } } catch {}
        try { $ev = @(Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2; StartTime=(Get-Date).AddDays(-7)} -MaxEvents 3000 -ErrorAction Stop); $r.SystemErrorCount = $ev.Count; $r.SystemErrors = @($ev | Group-Object ProviderName | Sort-Object Count -Descending | Select-Object -First 8 @{n='Source';e={$_.Name}}, Count); $r.Critical = @($ev | Where-Object { ($_.ProviderName -eq 'Microsoft-Windows-Kernel-Power' -and $_.Id -eq 41) -or ($_.ProviderName -like '*WER-SystemErrorReporting*') -or ($_.ProviderName -in 'disk','Ntfs','storahci','stornvme','iaStorA','iaStorAC') -or ($_.ProviderName -like '*WHEA*') } | Group-Object ProviderName | Select-Object @{n='Source';e={$_.Name}}, Count) } catch {}
        try { $rp = @(Get-ComputerRestorePoint -ErrorAction Stop); $r.RestorePoints = $rp.Count } catch {}
        $r | ConvertTo-Json -Depth 5 -Compress
        """;

    private static async Task<JsonElement?> QueryWmiAsync(CancellationToken ct)
    {
        var run = await CommandRunner.RunPowerShellAsync(WmiScript, TimeSpan.FromMinutes(2), cancellationToken: ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(run.Output.Trim());
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<DiagnosticFinding> InterpretWmi(JsonElement? data, bool isAdministrator)
    {
        if (data is not { } root)
        {
            yield return new DiagnosticFinding("Sistema", "Consultas do Windows", HealthStatus.Info,
                "Não foi possível consultar discos, drivers e segurança (PowerShell indisponível).");
            yield break;
        }

        // Saúde dos discos (SMART/Storage).
        foreach (var disk in JsonHelpers.Array(root, "Disks"))
        {
            string name = JsonHelpers.Str(disk, "Name") ?? "Disco";
            string health = JsonHelpers.Str(disk, "Health") ?? string.Empty;
            long temp = JsonHelpers.Long(disk, "Temp");
            long wear = JsonHelpers.Long(disk, "Wear");
            long hours = JsonHelpers.Long(disk, "Hours");
            long errors = JsonHelpers.Long(disk, "ReadErr") + JsonHelpers.Long(disk, "WriteErr");
            var details = new List<string> { $"Tipo: {JsonHelpers.Str(disk, "Media")} / {JsonHelpers.Str(disk, "Bus")}", $"Tamanho: {ByteSize.Format(JsonHelpers.Long(disk, "Size"))}" };
            if (temp > 0) details.Add($"Temperatura: {temp} °C");
            if (wear > 0) details.Add($"Desgaste: {wear}%");
            if (hours > 0) details.Add($"Horas ligado: {ByteSize.FormatCount(hours)}");
            if (errors > 0) details.Add($"Erros não corrigidos: {errors}");

            var status = !health.Equals("Healthy", StringComparison.OrdinalIgnoreCase) && health.Length > 0 ? HealthStatus.Problem
                : errors > 0 || wear >= 90 ? HealthStatus.Problem
                : wear >= 70 || temp >= 60 ? HealthStatus.Attention
                : HealthStatus.Ok;
            string summary = status switch
            {
                HealthStatus.Problem => $"O Windows indica problema de saúde ({health}). Faça backup dos seus arquivos AGORA.",
                HealthStatus.Attention => temp >= 60 ? $"Disco quente ({temp} °C)." : $"Disco com desgaste de {wear}%.",
                _ => "Saudável segundo o próprio disco (SMART).",
            };
            yield return new DiagnosticFinding("Disco", $"Saúde do disco: {name}", status, summary,
                status == HealthStatus.Ok ? null : "Mantenha backup atualizado e considere substituir o disco. Verifique o disco com CHKDSK.",
                details, status == HealthStatus.Ok ? null : "repair.chkdsk", "Verificar disco (CHKDSK)");
        }

        if (!isAdministrator && !JsonHelpers.Array(root, "Disks").Any())
        {
            yield return new DiagnosticFinding("Disco", "Saúde dos discos", HealthStatus.Info,
                "Para ler a saúde SMART dos discos, rode o diagnóstico como administrador.");
        }

        // Volumes com erro de sistema de arquivos pendente.
        var dirty = JsonHelpers.Array(root, "Volumes").Where(v => JsonHelpers.Bool(v, "DirtyBitSet")).Select(v => JsonHelpers.Str(v, "DriveLetter")).ToList();
        yield return dirty.Count == 0
            ? new DiagnosticFinding("Disco", "Sistema de arquivos (NTFS)", HealthStatus.Ok, "Nenhum volume marcado com erros.")
            : new DiagnosticFinding("Disco", "Sistema de arquivos (NTFS)", HealthStatus.Problem,
                $"O Windows marcou {string.Join(", ", dirty)} para verificação (erros no sistema de arquivos).",
                "Execute o CHKDSK para corrigir.", null, "repair.chkdsk", "Verificar disco (CHKDSK)");

        // Dispositivos com problema.
        var devices = JsonHelpers.Array(root, "Devices")
            .Select(d => $"{JsonHelpers.Str(d, "Name") ?? "Dispositivo"} (código {JsonHelpers.Long(d, "ConfigManagerErrorCode")}{DeviceCodeText(JsonHelpers.Long(d, "ConfigManagerErrorCode"))})")
            .ToList();
        yield return devices.Count == 0
            ? new DiagnosticFinding("Drivers", "Dispositivos e drivers", HealthStatus.Ok, "Nenhum dispositivo com erro no Gerenciador de Dispositivos.")
            : new DiagnosticFinding("Drivers", "Dispositivos e drivers", HealthStatus.Attention,
                $"{devices.Count} dispositivo(s) com problema.",
                "Atualize ou reinstale o driver pelo Gerenciador de Dispositivos ou pelo site do fabricante. Desconectados/desativados podem ser ignorados.",
                devices);

        // Windows Update.
        if (root.TryGetProperty("LastHotfix", out var hotfix) &&
            DateTime.TryParse(JsonHelpers.Str(hotfix, "Date"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastUpdate))
        {
            int days = (int)(DateTime.Now - lastUpdate).TotalDays;
            var status = days > 60 ? HealthStatus.Attention : HealthStatus.Ok;
            yield return new DiagnosticFinding("Sistema", "Windows Update", status,
                $"Última atualização instalada: {JsonHelpers.Str(hotfix, "Id")} em {lastUpdate:dd/MM/yyyy} ({days} dias).",
                status == HealthStatus.Ok ? null : "Abra o Windows Update e instale as atualizações. Se falhar sempre, use \"Redefinir o Windows Update\".",
                ActionId: status == HealthStatus.Ok ? null : "repair.wureset", ActionText: "Redefinir Windows Update");
        }

        // Antivírus.
        var products = JsonHelpers.Array(root, "AvProducts")
            .Select(p => (Name: JsonHelpers.Str(p, "displayName") ?? "?", State: JsonHelpers.Long(p, "productState")))
            .ToList();
        bool defenderOn = root.TryGetProperty("Defender", out var defender) && JsonHelpers.Bool(defender, "Rt");
        bool anyAvOn = defenderOn || products.Any(p => ((p.State >> 12) & 0xF) == 1);
        long sigAge = root.TryGetProperty("Defender", out var d2) ? JsonHelpers.Long(d2, "SigAge") : 0;
        yield return !anyAvOn
            ? new DiagnosticFinding("Segurança", "Antivírus", HealthStatus.Problem, "Nenhum antivírus com proteção em tempo real ativa.",
                "Ative o Microsoft Defender em Segurança do Windows > Proteção contra vírus e ameaças.",
                products.Select(p => p.Name).ToList())
            : new DiagnosticFinding("Segurança", "Antivírus", defenderOn && sigAge > 7 ? HealthStatus.Attention : HealthStatus.Ok,
                defenderOn ? $"Microsoft Defender ativo (definições com {sigAge} dia(s))." : $"Ativo: {string.Join(", ", products.Select(p => p.Name))}.",
                defenderOn && sigAge > 7 ? "As definições estão desatualizadas: rode o Windows Update." : null);

        // Firewall.
        var firewall = JsonHelpers.Array(root, "Firewall").Select(f => (Name: JsonHelpers.Str(f, "Name") ?? "?", On: JsonHelpers.Bool(f, "Enabled") || JsonHelpers.Long(f, "Enabled") == 1)).ToList();
        if (firewall.Count > 0)
        {
            var off = firewall.Where(f => !f.On).Select(f => f.Name).ToList();
            yield return off.Count == 0
                ? new DiagnosticFinding("Segurança", "Firewall do Windows", HealthStatus.Ok, "Ativado em todos os perfis de rede.")
                : new DiagnosticFinding("Segurança", "Firewall do Windows", HealthStatus.Attention,
                    $"Desativado nos perfis: {string.Join(", ", off)}.", "Ative o firewall (a menos que outro firewall esteja protegendo o PC).");
        }

        // Bateria.
        if (root.TryGetProperty("Battery", out var battery))
        {
            long design = JsonHelpers.Long(battery, "Design");
            long full = JsonHelpers.Long(battery, "Full");
            if (design > 0 && full > 0)
            {
                double health = Math.Min(1, full / (double)design);
                var status = health < 0.5 ? HealthStatus.Problem : health < 0.7 ? HealthStatus.Attention : HealthStatus.Ok;
                yield return new DiagnosticFinding("Energia", "Saúde da bateria", status,
                    $"A bateria guarda {ByteSize.FormatPercent(health)} da carga original ({full:N0} de {design:N0} mWh).",
                    status == HealthStatus.Ok ? null : "A bateria está desgastada; a autonomia será menor. Considere a troca.",
                    ActionId: "repair.batteryreport", ActionText: "Relatório de bateria");
            }
        }

        // Erros críticos no log do sistema (7 dias).
        var critical = JsonHelpers.Array(root, "Critical")
            .Select(c => (Source: JsonHelpers.Str(c, "Source") ?? "?", Count: JsonHelpers.Long(c, "Count"))).ToList();
        long totalErrors = JsonHelpers.Long(root, "SystemErrorCount");
        var topSources = JsonHelpers.Array(root, "SystemErrors").Select(s => $"{JsonHelpers.Str(s, "Source")}: {JsonHelpers.Long(s, "Count")}").ToList();
        if (critical.Count > 0)
        {
            var details = critical.Select(c => $"{DescribeCriticalSource(c.Source)}: {c.Count} ocorrência(s)").ToList();
            bool diskErrors = critical.Any(c => c.Source is "disk" or "Ntfs" or "storahci" or "stornvme" or "iaStorA" or "iaStorAC");
            yield return new DiagnosticFinding("Estabilidade", "Erros críticos nos últimos 7 dias", HealthStatus.Problem,
                "O log do sistema registrou desligamentos inesperados, telas azuis ou erros de disco/hardware.",
                diskErrors ? "Há erros de disco: faça backup e verifique o disco (CHKDSK)." : "Verifique drivers, temperatura e fonte. Rode o SFC/DISM para reparar o sistema.",
                details, diskErrors ? "repair.chkdsk" : "repair.sfc", diskErrors ? "Verificar disco (CHKDSK)" : "Reparar arquivos do sistema (SFC)");
        }
        else if (totalErrors > 0)
        {
            yield return new DiagnosticFinding("Estabilidade", "Erros no log do sistema (7 dias)", totalErrors > 200 ? HealthStatus.Attention : HealthStatus.Info,
                $"{totalErrors} erro(s) registrados, nenhum crítico (tela azul, desligamento inesperado ou disco).",
                totalErrors > 200 ? "Muitos erros repetidos costumam indicar um driver ou serviço com defeito." : null, topSources);
        }
        else if (root.TryGetProperty("SystemErrorCount", out _))
        {
            yield return new DiagnosticFinding("Estabilidade", "Erros no log do sistema (7 dias)", HealthStatus.Ok, "Nenhum erro registrado.");
        }

        if (root.TryGetProperty("RestorePoints", out var rp) && rp.ValueKind == JsonValueKind.Number)
        {
            int count = rp.GetInt32();
            yield return new DiagnosticFinding("Sistema", "Pontos de restauração", count == 0 ? HealthStatus.Attention : HealthStatus.Ok,
                count == 0 ? "Nenhum ponto de restauração disponível." : $"{count} ponto(s) de restauração disponível(is).",
                count == 0 ? "Ative a Proteção do Sistema para poder desfazer alterações de drivers e programas." : null);
        }
    }

    private static string DeviceCodeText(long code) => code switch
    {
        10 => ": não pode ser iniciado",
        22 => ": desativado",
        28 => ": driver não instalado",
        43 => ": o Windows parou o dispositivo",
        45 => ": não está conectado",
        _ => string.Empty,
    };

    private static string DescribeCriticalSource(string source) => source switch
    {
        "Microsoft-Windows-Kernel-Power" => "Desligamentos inesperados (Kernel-Power 41)",
        _ when source.Contains("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) => "Telas azuis (BugCheck)",
        _ when source.Contains("WHEA", StringComparison.OrdinalIgnoreCase) => "Erros de hardware (WHEA)",
        "disk" or "storahci" or "stornvme" or "iaStorA" or "iaStorAC" => $"Erros de disco/controladora ({source})",
        "Ntfs" => "Erros do sistema de arquivos (NTFS)",
        _ => source,
    };

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SystemPowerStatus
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
    private static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);
}
