using System.Diagnostics;
using System.Runtime.InteropServices;
using Headroom.Core.SystemTools;

namespace Headroom.Core.Optimization;

/// <summary>Retrato detalhado da memória do sistema.</summary>
public sealed record MemoryDetails(
    long TotalBytes,
    long AvailableBytes,
    long CommitTotal,
    long CommitLimit,
    long SystemCacheBytes,
    long KernelPagedBytes,
    long KernelNonPagedBytes,
    long? StandbyBytes,
    long? ModifiedBytes,
    long? FreeBytes,
    int ProcessCount,
    int ThreadCount,
    int HandleCount)
{
    public long UsedBytes => TotalBytes - AvailableBytes;
    public double UsedFraction => TotalBytes == 0 ? 0 : UsedBytes / (double)TotalBytes;
    public double CommitFraction => CommitLimit == 0 ? 0 : CommitTotal / (double)CommitLimit;
}

/// <summary>O que "Liberar memória" deve fazer.</summary>
[Flags]
public enum MemoryCleanupAreas
{
    None = 0,

    /// <summary>Reduz o working set dos programas em segundo plano (EmptyWorkingSet). Não exige administrador.</summary>
    WorkingSets = 1,

    /// <summary>Grava a lista "modificada" no disco antes de esvaziar o cache. Exige administrador.</summary>
    ModifiedList = 2,

    /// <summary>Esvazia a lista em espera (standby / cache de arquivos já lidos). Exige administrador.</summary>
    StandbyList = 4,

    /// <summary>Esvazia o cache do sistema de arquivos. Exige administrador.</summary>
    SystemFileCache = 8,
}

/// <summary>Resultado de "Liberar memória".</summary>
public sealed record MemoryCleanupResult(
    long AvailableBefore,
    long AvailableAfter,
    int ProcessesTrimmed,
    IReadOnlyList<string> Done,
    IReadOnlyList<string> Skipped)
{
    public long FreedBytes => Math.Max(0, AvailableAfter - AvailableBefore);
}

/// <summary>
/// Leitura detalhada da memória e "Liberar memória" do jeito que ferramentas como RAMMap e Mem Reduct fazem.
/// Importante: o Windows já usa a RAM livre como cache de forma inteligente; liberar memória só ajuda quando a
/// RAM está quase cheia (ex.: antes de abrir um jogo ou programa pesado). Os programas recarregam o que
/// precisam aos poucos, então pode haver uma lentidão breve logo depois.
/// </summary>
public static class MemoryOptimizer
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryFlushModifiedList = 3;
    private const int MemoryPurgeStandbyList = 4;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetQuota = 0x0100;

    /// <summary>Lê a memória. Standby/modificada/livre só aparecem como administrador (senão ficam nulas).</summary>
    public static MemoryDetails GetDetails()
    {
        var basic = SystemInfo.GetMemory();
        var perf = new PerformanceInformation { cb = (uint)Marshal.SizeOf<PerformanceInformation>() };
        bool hasPerf = GetPerformanceInfo(ref perf, perf.cb);
        long page = hasPerf ? (long)perf.PageSize : Environment.SystemPageSize;

        long? standby = null, modified = null, free = null;
        if (TryQueryMemoryLists(out var lists))
        {
            long standbyPages = 0;
            for (int i = 0; i < 8; i++)
            {
                standbyPages += (long)lists.GetPriority(i);
            }

            standby = standbyPages * page;
            modified = (long)lists.ModifiedPageCount * page;
            free = ((long)lists.ZeroPageCount + (long)lists.FreePageCount) * page;
        }

        return new MemoryDetails(
            basic.TotalBytes,
            basic.AvailableBytes,
            hasPerf ? (long)perf.CommitTotal * page : basic.CommitTotal,
            hasPerf ? (long)perf.CommitLimit * page : basic.CommitLimit,
            hasPerf ? (long)perf.SystemCache * page : 0,
            hasPerf ? (long)perf.KernelPaged * page : 0,
            hasPerf ? (long)perf.KernelNonpaged * page : 0,
            standby,
            modified,
            free,
            hasPerf ? (int)perf.ProcessCount : 0,
            hasPerf ? (int)perf.ThreadCount : 0,
            hasPerf ? (int)perf.HandleCount : 0);
    }

    /// <summary>Executa a liberação escolhida e mede quanto ficou disponível a mais.</summary>
    public static Task<MemoryCleanupResult> FreeAsync(MemoryCleanupAreas areas, CancellationToken cancellationToken = default) =>
        Task.Run(() => Free(areas, cancellationToken), cancellationToken);

    private static MemoryCleanupResult Free(MemoryCleanupAreas areas, CancellationToken ct)
    {
        long before = SystemInfo.GetMemory().AvailableBytes;
        var done = new List<string>();
        var skipped = new List<string>();
        int trimmed = 0;

        if (areas.HasFlag(MemoryCleanupAreas.WorkingSets))
        {
            trimmed = TrimBackgroundWorkingSets(ct);
            done.Add($"Memória de {trimmed} programa(s) em segundo plano reduzida");
        }

        bool canManageLists = Privileges.TryEnable(Privileges.ProfileSingleProcess);
        if (areas.HasFlag(MemoryCleanupAreas.ModifiedList))
        {
            if (canManageLists && SetMemoryListCommand(MemoryFlushModifiedList))
            {
                done.Add("Lista modificada gravada no disco");
            }
            else
            {
                skipped.Add("Lista modificada (requer administrador)");
            }
        }

        if (areas.HasFlag(MemoryCleanupAreas.SystemFileCache))
        {
            if (Privileges.TryEnable(Privileges.IncreaseQuota) &&
                SetSystemFileCacheSize(new UIntPtr(ulong.MaxValue), new UIntPtr(ulong.MaxValue), 0))
            {
                done.Add("Cache do sistema de arquivos esvaziado");
            }
            else
            {
                skipped.Add("Cache do sistema de arquivos (requer administrador)");
            }
        }

        if (areas.HasFlag(MemoryCleanupAreas.StandbyList))
        {
            if (canManageLists && SetMemoryListCommand(MemoryPurgeStandbyList))
            {
                done.Add("Lista em espera (standby) esvaziada");
            }
            else
            {
                skipped.Add("Lista em espera (requer administrador)");
            }
        }

        // O Windows leva um instante para devolver as páginas à lista livre.
        Thread.Sleep(700);
        long after = SystemInfo.GetMemory().AvailableBytes;
        return new MemoryCleanupResult(before, after, trimmed, done, skipped);
    }

    /// <summary>
    /// Reduz o working set de cada processo que dá para abrir, menos o programa em primeiro plano (para não
    /// deixar lento o que o usuário está usando) e o próprio Headroom.
    /// </summary>
    private static int TrimBackgroundWorkingSets(CancellationToken ct)
    {
        int foreground = ForegroundProcessId();
        int self = Environment.ProcessId;
        int count = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (process.Id is 0 or 4 || process.Id == foreground || process.Id == self)
                {
                    continue;
                }

                IntPtr handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetQuota, false, (uint)process.Id);
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (EmptyWorkingSet(handle))
                    {
                        count++;
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }

        return count;
    }

    private static int ForegroundProcessId()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return -1;
        }

        GetWindowThreadProcessId(window, out uint pid);
        return (int)pid;
    }

    private static bool TryQueryMemoryLists(out MemoryListInformation info)
    {
        info = default;
        if (!Privileges.TryEnable(Privileges.ProfileSingleProcess))
        {
            return false;
        }

        int status = NtQuerySystemInformation(SystemMemoryListInformation, out info, Marshal.SizeOf<MemoryListInformation>(), out _);
        return status == 0;
    }

    private static bool SetMemoryListCommand(int command)
    {
        int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        return status == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    /// <summary>SYSTEM_MEMORY_LIST_INFORMATION (contagens em páginas).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryListInformation
    {
        public UIntPtr ZeroPageCount;
        public UIntPtr FreePageCount;
        public UIntPtr ModifiedPageCount;
        public UIntPtr ModifiedNoWritePageCount;
        public UIntPtr BadPageCount;
        public UIntPtr Priority0;
        public UIntPtr Priority1;
        public UIntPtr Priority2;
        public UIntPtr Priority3;
        public UIntPtr Priority4;
        public UIntPtr Priority5;
        public UIntPtr Priority6;
        public UIntPtr Priority7;
        public UIntPtr Repurposed0;
        public UIntPtr Repurposed1;
        public UIntPtr Repurposed2;
        public UIntPtr Repurposed3;
        public UIntPtr Repurposed4;
        public UIntPtr Repurposed5;
        public UIntPtr Repurposed6;
        public UIntPtr Repurposed7;
        public UIntPtr ModifiedPageCountPageFile;

        public readonly ulong GetPriority(int index) => index switch
        {
            0 => (ulong)Priority0,
            1 => (ulong)Priority1,
            2 => (ulong)Priority2,
            3 => (ulong)Priority3,
            4 => (ulong)Priority4,
            5 => (ulong)Priority5,
            6 => (ulong)Priority6,
            _ => (ulong)Priority7,
        };
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemFileCacheSize(UIntPtr minimum, UIntPtr maximum, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, out MemoryListInformation info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int command, int length);
}
