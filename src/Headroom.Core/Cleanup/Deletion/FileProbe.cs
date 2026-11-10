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

    public static ProbeResult Probe(string path) => Probe(path, wantIdentity: false, out _);

    /// <summary>
    /// Igual a <see cref="Probe(string)"/> e, quando o item pode ser apagado, diz qual arquivo ele é (volume e file
    /// ID, lidos do mesmo handle): dois hardlinks do mesmo arquivo têm a mesma identidade.
    /// </summary>
    internal static ProbeResult Probe(string path, out FileIdentity identity) => Probe(path, wantIdentity: true, out identity);

    private static ProbeResult Probe(string path, bool wantIdentity, out FileIdentity identity)
    {
        identity = default;
        using SafeFileHandle handle = CreateFileW(path, Delete, ShareAll, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            if (wantIdentity && GetFileInformationByHandle(handle, out var info))
            {
                identity = new FileIdentity(info.VolumeSerialNumber, ((long)info.FileIndexHigh << 32) | info.FileIndexLow);
            }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    /// <summary>BY_HANDLE_FILE_INFORMATION (FILETIMEs como pares de DWORD: alinhamento de 4 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct ByHandleFileInformation
    {
        [FieldOffset(28)] public uint VolumeSerialNumber;
        [FieldOffset(44)] public uint FileIndexHigh;
        [FieldOffset(48)] public uint FileIndexLow;
    }
}

/// <summary>Qual arquivo um caminho é: serial do volume e file ID (0 = desconhecido).</summary>
internal readonly record struct FileIdentity(uint VolumeSerial, long FileIndex);
