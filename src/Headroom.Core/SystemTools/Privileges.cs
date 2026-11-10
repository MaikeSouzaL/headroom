using System.Runtime.InteropServices;

namespace Headroom.Core.SystemTools;

/// <summary>Ativa privilégios do token do próprio processo (só funcionam se o usuário os possuir, ex.: administrador).</summary>
internal static class Privileges
{
    public const string ProfileSingleProcess = "SeProfileSingleProcessPrivilege";
    public const string IncreaseQuota = "SeIncreaseQuotaPrivilege";

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    /// <summary>Tenta ativar o privilégio; falso se o usuário não o possui.</summary>
    public static bool TryEnable(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
            {
                return false;
            }

            var state = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }

            return Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);
}
