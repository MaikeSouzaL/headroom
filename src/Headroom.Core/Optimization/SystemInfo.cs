using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Headroom.Core.SystemTools;

namespace Headroom.Core.Optimization;

/// <summary>Uso de memória do sistema.</summary>
public sealed record MemoryInfo(long TotalBytes, long AvailableBytes, long CommitTotal, long CommitLimit)
{
    public long UsedBytes => TotalBytes - AvailableBytes;
    public double UsedFraction => TotalBytes == 0 ? 0 : UsedBytes / (double)TotalBytes;
}

/// <summary>Processo com consumo medido.</summary>
public sealed record ProcessUsage(int Id, string Name, long WorkingSet, double CpuPercent, string? Path, bool IsProtected);

/// <summary>Plano de energia do Windows.</summary>
public sealed record PowerPlan(Guid Id, string Name, bool IsActive);

/// <summary>Medições do sistema (memória, CPU, processos) e plano de energia.</summary>
public static partial class SystemInfo
{
    /// <summary>Processos que nunca podem ser finalizados pelo app (finalizá-los derruba o Windows).</summary>
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm",
        "fontdrvhost", "Registry", "Memory Compression", "MsMpEng", "NisSrv", "SecurityHealthService", "sihost",
        "ctfmon", "conhost", "spoolsv", "audiodg", "WUDFHost", "LsaIso", "SgrmBroker", "MemCompression",
    };

    /// <summary>Verdadeiro para processos essenciais do Windows e serviços (sessão 0).</summary>
    public static bool IsProtectedProcess(string name, int sessionId) =>
        sessionId == 0 || ProtectedProcesses.Contains(name);

    /// <summary>Como acima, e também o próprio Headroom (outra janela dele pode ser finalizada normalmente).</summary>
    public static bool IsProtectedProcess(int id, string name, int sessionId) =>
        id == Environment.ProcessId || IsProtectedProcess(name, sessionId);

    /// <summary>Finaliza um processo pelo Id, conferindo o nome para não matar outro que reaproveitou o Id.</summary>
    public static void KillProcess(int id, string expectedName, bool entireTree = true)
    {
        using var process = Process.GetProcessById(id);
        if (!string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"O processo \"{expectedName}\" já foi encerrado.");
        }

        if (IsProtectedProcess(process.Id, process.ProcessName, process.SessionId))
        {
            throw new InvalidOperationException("Este processo é essencial para o Windows (ou é um serviço) e não pode ser finalizado.");
        }

        process.Kill(entireTree);
    }

    /// <summary>Muda a prioridade de um processo (ex.: "Abaixo do normal" para algo pesado em segundo plano).</summary>
    public static void SetPriority(int id, string expectedName, ProcessPriorityClass priority)
    {
        if (priority == ProcessPriorityClass.RealTime)
        {
            throw new ArgumentException("Tempo real pode travar o PC e não é oferecido.", nameof(priority));
        }

        using var process = Process.GetProcessById(id);
        if (!string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"O processo \"{expectedName}\" já foi encerrado.");
        }

        if (IsProtectedProcess(process.Id, process.ProcessName, process.SessionId))
        {
            throw new InvalidOperationException("A prioridade de processos essenciais do Windows não é alterada.");
        }

        process.PriorityClass = priority;
    }

    /// <summary>Nome do processador (registro) e frequência nominal em MHz.</summary>
    public static (string Name, int Mhz) GetProcessorInfo()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processador";
            int mhz = key?.GetValue("~MHz") is int value ? value : 0;
            return (Regex.Replace(name, @"\s{2,}", " "), mhz);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return ("Processador", 0);
        }
    }

    public static MemoryInfo GetMemory()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return new MemoryInfo(0, 0, 0, 0);
        }

        return new MemoryInfo((long)status.ullTotalPhys, (long)status.ullAvailPhys,
            (long)(status.ullTotalPageFile - status.ullAvailPageFile), (long)status.ullTotalPageFile);
    }

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Uso total de CPU (0..1) medido durante <paramref name="sample"/>.</summary>
    public static async Task<double> MeasureCpuAsync(TimeSpan sample, CancellationToken ct = default)
    {
        if (!GetSystemTimes(out var idle1, out var kernel1, out var user1))
        {
            return 0;
        }

        await Task.Delay(sample, ct).ConfigureAwait(false);
        if (!GetSystemTimes(out var idle2, out var kernel2, out var user2))
        {
            return 0;
        }

        long idle = ToLong(idle2) - ToLong(idle1);
        long total = (ToLong(kernel2) - ToLong(kernel1)) + (ToLong(user2) - ToLong(user1));
        return total <= 0 ? 0 : Math.Clamp(1 - idle / (double)total, 0, 1);
    }

    /// <summary>Processos que mais usam memória e CPU (CPU medida durante a amostra).</summary>
    public static async Task<List<ProcessUsage>> TopProcessesAsync(TimeSpan sample, CancellationToken ct = default)
    {
        var first = new Dictionary<int, TimeSpan>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                first[p.Id] = p.TotalProcessorTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        await Task.Delay(sample, ct).ConfigureAwait(false);
        var result = new List<ProcessUsage>();
        int cores = Environment.ProcessorCount;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                double cpu = 0;
                if (first.TryGetValue(p.Id, out var before))
                {
                    try
                    {
                        cpu = (p.TotalProcessorTime - before).TotalMilliseconds / sample.TotalMilliseconds / cores;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }

                string? path = null;
                try
                {
                    path = p.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                result.Add(new ProcessUsage(p.Id, p.ProcessName, p.WorkingSet64, Math.Clamp(cpu, 0, 1), path,
                    IsProtectedProcess(p.Id, p.ProcessName, p.SessionId)));
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        return result;
    }

    /// <summary>Finaliza um processo (nunca os protegidos do sistema).</summary>
    public static void KillProcess(ProcessUsage usage)
    {
        if (usage.IsProtected)
        {
            throw new InvalidOperationException("Este processo é essencial para o Windows e não pode ser finalizado.");
        }

        using var process = Process.GetProcessById(usage.Id);
        process.Kill(entireProcessTree: true);
    }

    /// <summary>Lista os planos de energia (powercfg /list).</summary>
    public static async Task<List<PowerPlan>> GetPowerPlansAsync(CancellationToken ct = default)
    {
        var run = await CommandRunner.RunAsync(CommandRunner.SystemTool("powercfg.exe"), "/list", TimeSpan.FromSeconds(20),
            cancellationToken: ct).ConfigureAwait(false);
        var plans = new List<PowerPlan>();
        foreach (Match m in PowerPlanRegex().Matches(run.Output))
        {
            if (Guid.TryParse(m.Groups["id"].Value, out var id))
            {
                plans.Add(new PowerPlan(id, m.Groups["name"].Value.Trim(), m.Groups["active"].Success));
            }
        }

        return plans;
    }

    public static async Task<bool> SetActivePowerPlanAsync(Guid id, CancellationToken ct = default)
    {
        var run = await CommandRunner.RunAsync(CommandRunner.SystemTool("powercfg.exe"), $"/setactive {id}", TimeSpan.FromSeconds(20),
            cancellationToken: ct).ConfigureAwait(false);
        return run.Success;
    }

    private static long ToLong(FileTime ft) => ((long)ft.High << 32) | ft.Low;

    [GeneratedRegex(@"(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((?<name>[^)]*)\)\s*(?<active>\*)?")]
    private static partial Regex PowerPlanRegex();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
}
