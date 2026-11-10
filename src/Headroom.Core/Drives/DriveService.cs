using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Headroom.Core.Drives;

public enum DriveMediaKind
{
    Unknown,
    Ssd,
    Hdd,
    Usb,
    Network,
    Optical,
    Removable,
    RamDisk,
}

/// <summary>Resumo de um disco para a tela inicial.</summary>
public sealed record DriveSummary(
    string RootPath,
    string VolumeLabel,
    string FileSystem,
    DriveType DriveType,
    DriveMediaKind MediaKind,
    string? Model,
    long TotalBytes,
    long FreeBytes,
    bool IsSystemDrive)
{
    public string Letter => RootPath.TrimEnd('\\');
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedFraction => TotalBytes <= 0 ? 0 : UsedBytes / (double)TotalBytes;
    public double FreeFraction => 1 - UsedFraction;
    public bool IsCritical => UsedFraction > 0.90;

    public string DisplayName => string.IsNullOrWhiteSpace(VolumeLabel)
        ? (DriveType == DriveType.Network ? "Unidade de rede" : "Disco local")
        : VolumeLabel;

    public string MediaKindText => MediaKind switch
    {
        DriveMediaKind.Ssd => "SSD",
        DriveMediaKind.Hdd => "HD",
        DriveMediaKind.Usb => "USB",
        DriveMediaKind.Network => "Rede",
        DriveMediaKind.Optical => "CD/DVD",
        DriveMediaKind.Removable => "Removível",
        DriveMediaKind.RamDisk => "RAM disk",
        _ => "Desconhecido",
    };
}

/// <summary>Lista os discos prontos e identifica o tipo de mídia (SSD/HD/USB/rede).</summary>
public static class DriveService
{
    public static IReadOnlyList<DriveSummary> GetDrives()
    {
        var result = new List<DriveSummary>();
        string systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var (kind, model) = DetectMedia(drive);
                result.Add(new DriveSummary(
                    drive.RootDirectory.FullName,
                    SafeLabel(drive),
                    SafeFormat(drive),
                    drive.DriveType,
                    kind,
                    model,
                    drive.TotalSize,
                    drive.TotalFreeSpace,
                    drive.Name.Equals(systemRoot, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disco removido ou sem permissão entre a listagem e a leitura: ignora.
            }
        }

        return result;
    }

    public static DriveSummary? GetDrive(string rootPath) =>
        GetDrives().FirstOrDefault(d => d.RootPath.Equals(Path.GetPathRoot(rootPath), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Verdadeiro se o disco que contém o caminho é mecânico (o Windows informa "seek penalty"), inclusive HD
    /// externo USB; null quando não dá para saber (rede, sem permissão).
    /// </summary>
    public static bool? HasSeekPenalty(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            using var handle = Native.CreateFileW(@"\\.\" + root.TrimEnd('\\'), 0, Native.FileShareRead | Native.FileShareWrite,
                IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
            return handle.IsInvalid ? null : QuerySeekPenalty(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string SafeFormat(DriveInfo drive)
    {
        try
        {
            return drive.DriveFormat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static (DriveMediaKind Kind, string? Model) DetectMedia(DriveInfo drive)
    {
        switch (drive.DriveType)
        {
            case DriveType.Network:
                return (DriveMediaKind.Network, null);
            case DriveType.CDRom:
                return (DriveMediaKind.Optical, null);
            case DriveType.Ram:
                return (DriveMediaKind.RamDisk, null);
        }

        string devicePath = @"\\.\" + drive.Name.TrimEnd('\\');
        using var handle = Native.CreateFileW(devicePath, 0, Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return (drive.DriveType == DriveType.Removable ? DriveMediaKind.Removable : DriveMediaKind.Unknown, null);
        }

        var (busType, model) = QueryBusType(handle);
        if (busType == Native.BusTypeUsb)
        {
            return (DriveMediaKind.Usb, model);
        }

        if (drive.DriveType == DriveType.Removable)
        {
            return (DriveMediaKind.Removable, model);
        }

        if (busType == Native.BusTypeNvme)
        {
            return (DriveMediaKind.Ssd, model);
        }

        bool? seekPenalty = QuerySeekPenalty(handle);
        return seekPenalty switch
        {
            false => (DriveMediaKind.Ssd, model),
            true => (DriveMediaKind.Hdd, model),
            _ => (DriveMediaKind.Unknown, model),
        };
    }

    private static bool? QuerySeekPenalty(SafeFileHandle handle)
    {
        var query = new Native.StoragePropertyQuery
        {
            PropertyId = Native.StorageDeviceSeekPenaltyProperty,
            QueryType = Native.PropertyStandardQuery,
        };

        bool ok = Native.DeviceIoControl(handle, Native.IoctlStorageQueryProperty, ref query,
            Marshal.SizeOf<Native.StoragePropertyQuery>(), out Native.DeviceSeekPenaltyDescriptor descriptor,
            Marshal.SizeOf<Native.DeviceSeekPenaltyDescriptor>(), out _, IntPtr.Zero);
        return ok ? descriptor.IncursSeekPenalty != 0 : null;
    }

    private static (int BusType, string? Model) QueryBusType(SafeFileHandle handle)
    {
        var query = new Native.StoragePropertyQuery
        {
            PropertyId = Native.StorageDeviceProperty,
            QueryType = Native.PropertyStandardQuery,
        };

        const int bufferSize = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!Native.DeviceIoControl(handle, Native.IoctlStorageQueryProperty, ref query,
                    Marshal.SizeOf<Native.StoragePropertyQuery>(), buffer, bufferSize, out int returned, IntPtr.Zero) ||
                returned < 32)
            {
                return (-1, null);
            }

            // STORAGE_DEVICE_DESCRIPTOR: ProductIdOffset em 16, BusType em 28.
            int productOffset = Marshal.ReadInt32(buffer, 16);
            int busType = Marshal.ReadInt32(buffer, 28);
            string? model = productOffset > 0 && productOffset < returned
                ? Marshal.PtrToStringAnsi(buffer + productOffset)?.Trim()
                : null;
            return (busType, string.IsNullOrWhiteSpace(model) ? null : model);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class Native
    {
        public const uint FileShareRead = 0x1;
        public const uint FileShareWrite = 0x2;
        public const uint OpenExisting = 3;
        public const uint IoctlStorageQueryProperty = 0x002D1400;
        public const int StorageDeviceProperty = 0;
        public const int StorageDeviceSeekPenaltyProperty = 7;
        public const int PropertyStandardQuery = 0;
        public const int BusTypeUsb = 7;
        public const int BusTypeNvme = 17;

        [StructLayout(LayoutKind.Sequential)]
        public struct StoragePropertyQuery
        {
            public int PropertyId;
            public int QueryType;
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceSeekPenaltyDescriptor
        {
            public uint Version;
            public uint Size;
            public byte IncursSeekPenalty;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
            ref StoragePropertyQuery inBuffer, int inBufferSize, out DeviceSeekPenaltyDescriptor outBuffer,
            int outBufferSize, out int bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
            ref StoragePropertyQuery inBuffer, int inBufferSize, IntPtr outBuffer,
            int outBufferSize, out int bytesReturned, IntPtr overlapped);
    }
}
