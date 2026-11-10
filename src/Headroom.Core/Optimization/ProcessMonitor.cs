using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Headroom.Core.Optimization;

/// <summary>Motivo para um processo ser considerado "pesado".</summary>
public enum ProcessIssue
{
    HighCpu,
    HighMemory,
    HighDisk,
    NotResponding,
}

/// <summary>Medição de um processo num instante.</summary>
public sealed record ProcessSample(
    int Id,
    string Name,
    string? Description,
    string? Company,
    string? Path,
    string? WindowTitle,
    double CpuFraction,
    double CpuAverage,
    long WorkingSet,
    long PrivateBytes,
    double DiskBytesPerSecond,
    bool NotResponding,
    ProcessPriorityClass? Priority,
    bool IsProtected,
    IReadOnlyList<ProcessIssue> Issues)
{
    public bool IsHeavy => Issues.Count > 0;

    /// <summary>Nome amigável: a descrição do executável quando houver (ex.: "Google Chrome").</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description!;
}

/// <summary>Uso total do processador medido entre duas chamadas (GetSystemTimes).</summary>
public sealed class CpuSampler
{
    private long _idle, _kernel, _user;
    private bool _hasPrevious;

    /// <summary>Uso (0..1) desde a chamada anterior; a primeira chamada devolve 0.</summary>
    public double Next()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return 0;
        }

        double result = 0;
        if (_hasPrevious)
        {
            long total = (kernel - _kernel) + (user - _user);
            result = total <= 0 ? 0 : Math.Clamp(1 - (idle - _idle) / (double)total, 0, 1);
        }

        (_idle, _kernel, _user, _hasPrevious) = (idle, kernel, user, true);
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}

/// <summary>
/// Mede CPU, memória e disco de cada processo entre amostras e aponta os que estão deixando o PC lento:
/// CPU alta sustentada (média das últimas 3 amostras), muita memória, muito disco ou janela sem responder.
/// </summary>
public sealed class ProcessMonitor
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly ConcurrentDictionary<string, (string? Description, string? Company)> VersionCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, State> _states = new();
    private long _lastTimestamp;

    /// <summary>CPU média (fração do total do PC) a partir da qual o processo é "pesado". Padrão: 15%.</summary>
    public double HighCpuFraction { get; init; } = 0.15;

    /// <summary>Disco (leitura + escrita) a partir do qual o processo é "pesado". Padrão: 20 MB/s.</summary>
    public double HighDiskBytesPerSecond { get; init; } = 20 * 1024 * 1024;

    /// <summary>Memória a partir da qual o processo é "pesado". Padrão: o maior entre 1,5 GB e 12% da RAM.</summary>
    public long HighMemoryBytes { get; init; } =
        Math.Max(1536L * 1024 * 1024, (long)(SystemInfo.GetMemory().TotalBytes * 0.12));

    public IReadOnlyList<ProcessSample> Sample()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = _lastTimestamp == 0 ? 0 : (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;
        int cores = Environment.ProcessorCount;
        var windows = CollectWindows();
        var result = new List<ProcessSample>();
        var alive = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == 0)
                {
                    continue;
                }

                try
                {
                    alive.Add(process.Id);
                    result.Add(Measure(process, seconds, cores, windows));
                }
                catch (InvalidOperationException)
                {
                    // O processo terminou durante a leitura.
                }
            }
        }

        foreach (int gone in _states.Keys.Where(id => !alive.Contains(id)).ToList())
        {
            _states.Remove(gone);
        }

        return result;
    }

    private ProcessSample Measure(Process process, double seconds, int cores, Dictionary<int, (string Title, bool Hung)> windows)
    {
        int id = process.Id;
        string name = process.ProcessName;
        long workingSet = process.WorkingSet64;
        long privateBytes = process.PrivateMemorySize64;
        bool isProtected = SystemInfo.IsProtectedProcess(id, name, process.SessionId);

        long cpuTicks = 0, created = 0;
        ulong ioBytes = 0;
        bool hasCounters = false;
        ProcessPriorityClass? priority = null;
        string? path = null;

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)id);
        if (handle != IntPtr.Zero)
        {
            try
            {
                if (GetProcessTimes(handle, out created, out _, out long kernel, out long user))
                {
                    cpuTicks = kernel + user;
                    hasCounters = true;
                }

                if (GetProcessIoCounters(handle, out var io))
                {
                    ioBytes = io.ReadTransferCount + io.WriteTransferCount;
                }

                uint priorityClass = GetPriorityClass(handle);
                if (priorityClass != 0)
                {
                    priority = (ProcessPriorityClass)priorityClass;
                }

                if (!_states.TryGetValue(id, out var cached) || cached.Created != created)
                {
                    path = QueryImagePath(handle);
                }
                else
                {
                    path = cached.Path;
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        double cpu = 0, disk = 0;
        if (_states.TryGetValue(id, out var state) && state.Created == created && hasCounters && seconds > 0)
        {
            cpu = Math.Clamp((cpuTicks - state.CpuTicks) / 1e7 / seconds / cores, 0, 1);
            disk = Math.Max(0, (double)(ioBytes - Math.Min(ioBytes, state.IoBytes)) / seconds);
        }
        else
        {
            state = new State { Created = created };
            _states[id] = state;
        }

        state.CpuTicks = cpuTicks;
        state.IoBytes = ioBytes;
        state.Path = path;
        state.PushCpu(cpu);
        state.PushDisk(disk);

        (string? description, string? company) = path is null ? (null, null) : VersionCache.GetOrAdd(path, ReadVersionInfo);
        windows.TryGetValue(id, out var window);

        var issues = new List<ProcessIssue>();
        if (!isProtected)
        {
            if (window.Hung)
            {
                issues.Add(ProcessIssue.NotResponding);
            }

            if (state.CpuAverage >= HighCpuFraction)
            {
                issues.Add(ProcessIssue.HighCpu);
            }

            if (workingSet >= HighMemoryBytes)
            {
                issues.Add(ProcessIssue.HighMemory);
            }

            if (state.DiskAverage >= HighDiskBytesPerSecond)
            {
                issues.Add(ProcessIssue.HighDisk);
            }
        }

        return new ProcessSample(id, name, description, company, path, window.Title, cpu, state.CpuAverage, workingSet,
            privateBytes, state.DiskAverage, window.Hung, priority, isProtected, issues);
    }

    private static (string? Description, string? Company) ReadVersionInfo(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            string? description = string.IsNullOrWhiteSpace(info.FileDescription) ? null : info.FileDescription.Trim();
            string? company = string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName.Trim();
            return (description, company);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    private static string? QueryImagePath(IntPtr handle)
    {
        var buffer = new StringBuilder(1024);
        int size = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    /// <summary>Janelas visíveis de nível superior: título e se estão "Não respondendo", por processo.</summary>
    private static Dictionary<int, (string Title, bool Hung)> CollectWindows()
    {
        var windows = new Dictionary<int, (string Title, bool Hung)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero)
            {
                return true;
            }

            int length = GetWindowTextLength(hwnd);
            if (length == 0)
            {
                return true;
            }

            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            GetWindowThreadProcessId(hwnd, out uint pid);
            bool hung = IsHungAppWindow(hwnd);
            if (!windows.TryGetValue((int)pid, out var existing) || (hung && !existing.Hung))
            {
                windows[(int)pid] = (title.ToString(), hung || existing.Hung);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private sealed class State
    {
        private readonly double[] _cpu = new double[3];
        private readonly double[] _disk = new double[3];
        private int _count;

        public long Created { get; init; }
        public long CpuTicks { get; set; }
        public ulong IoBytes { get; set; }
        public string? Path { get; set; }

        public double CpuAverage => _count == 0 ? 0 : _cpu.Take(Math.Min(_count, 3)).Average();
        public double DiskAverage => _count == 0 ? 0 : _disk.Take(Math.Min(_count, 3)).Average();

        public void PushCpu(double value) => _cpu[_count % 3] = value;

        public void PushDisk(double value)
        {
            _disk[_count % 3] = value;
            _count++;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(IntPtr hwnd);
}
