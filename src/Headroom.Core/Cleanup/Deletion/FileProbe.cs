using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Headroom.Core.Cleanup.Deletion;

public enum ProbeResult
{
    Deletable,
    InUse,
    AccessDenied,
    NotFound,
}

/// <summary>
/// Testa, sem apagar, se um item pode ser removido agora: abre com acesso DELETE e
/// compartilhamento total. Se outro processo o mantém aberto sem permitir exclusão, a abertura
/// falha com violação de compartilhamento — assim arquivos em uso são pulados sem travar e sem
/// diálogos do Windows.
/// </summary>
public static class FileProbe
{
    private const uint Delete = 0x00010000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;

    public static ProbeResult Probe(string path)
    {
        using SafeFileHandle handle = CreateFileW(path, Delete, ShareAll, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return ProbeResult.Deletable;
        }

        return Marshal.GetLastWin32Error() switch
        {
            32 or 33 => ProbeResult.InUse,          // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
            2 or 3 => ProbeResult.NotFound,         // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
            _ => ProbeResult.AccessDenied,
        };
    }

    /// <summary>Discos fixos têm Lixeira; pendrives e unidades de rede não (o Windows apagaria de vez).</summary>
    public static bool SupportsRecycleBin(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
