using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Headroom.Core.Cleanup.Rules;

/// <summary>
/// Listagem de pasta com o file ID de cada arquivo, sem abrir os arquivos: em NTFS local usa
/// <c>GetFileInformationByHandleEx(FileIdBothDirectoryInfo)</c>, que devolve as mesmas entradas, na mesma ordem e com
/// os mesmos dados do <see cref="System.IO.Enumeration.FileSystemEnumerable{TResult}"/> (ambos usam
/// NtQueryDirectoryFile), mais o file ID. Usada pela análise (<see cref="ContentWalker"/>) e pela limpeza
/// (<see cref="Deletion.FileDeleter"/>) para contar cada arquivo uma vez (<see cref="HardLinkSet"/>).
/// </summary>
internal static unsafe class DirectoryReader
{
    /// <summary>Tamanho do buffer de listagem (um por thread).</summary>
    public const int BufferSize = 32 * 1024;

    /// <summary>Monta o resultado de uma entrada (sem "." e ".."); <paramref name="lastWriteTimeUtc"/> é FILETIME.</summary>
    public delegate T Transform<T>(ReadOnlySpan<char> name, FileAttributes attributes, long length, long lastWriteTimeUtc,
        long fileId);

    /// <summary>Buffer fixo na memória (vai direto para o kernel).</summary>
    public static byte[] NewBuffer() => GC.AllocateUninitializedArray<byte>(BufferSize, pinned: true);

    /// <summary>
    /// Lista <paramref name="directory"/> (caminho completo e normalizado) em <paramref name="into"/>. Devolve false,
    /// com o erro do Windows em <paramref name="openError"/>, se a pasta não pôde ser aberta; erro no meio da leitura
    /// vira <see cref="IOException"/>.
    /// </summary>
    public static bool TryList<T>(string directory, byte[] buffer, Transform<T> transform, List<T> into,
        CancellationToken ct, out int openError)
    {
        using var handle = OpenDirectory(directory);
        if (handle.IsInvalid)
        {
            openError = Marshal.GetLastPInvokeError();
            return false;
        }

        openError = 0;
        fixed (byte* start = buffer)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!Native.GetFileInformationByHandleEx(handle, Native.FileIdBothDirectoryInfo, start, (uint)buffer.Length))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is Native.ErrorNoMoreFiles or Native.ErrorFileNotFound)
                    {
                        break;
                    }

                    throw new IOException($"Falha ao listar {directory} (erro {error}).", Marshal.GetHRForLastWin32Error());
                }

                byte* record = start;
                while (true)
                {
                    var info = (Native.FileIdBothDirInfo*)record;
                    var name = new ReadOnlySpan<char>(record + Native.FileNameOffset, (int)(info->FileNameLength / sizeof(char)));
                    if (!name.SequenceEqual(".") && !name.SequenceEqual(".."))
                    {
                        into.Add(transform(name, (FileAttributes)info->FileAttributes, info->EndOfFile, info->LastWriteTime,
                            info->FileId));
                    }

                    if (info->NextEntryOffset == 0)
                    {
                        break;
                    }

                    record += info->NextEntryOffset;
                }
            }
        }

        return true;
    }

    /// <summary>Exceção equivalente à do .NET para uma pasta que não pôde ser aberta.</summary>
    public static Exception OpenFailure(string directory, int error) => error switch
    {
        Native.ErrorFileNotFound or Native.ErrorPathNotFound => new DirectoryNotFoundException($"Pasta não encontrada: {directory}"),
        Native.ErrorAccessDenied => new UnauthorizedAccessException($"Acesso negado: {directory}"),
        _ => new IOException($"Falha ao abrir {directory} (erro {error}).", unchecked((int)0x80070000) | error),
    };

    /// <summary>Mesma abertura do FileSystemEnumerator (segue a pasta-raiz se ela for junction, como antes).</summary>
    public static SafeFileHandle OpenDirectory(string directory) =>
        Native.CreateFileW(@"\\?\" + directory, Native.FileListDirectory | Native.Synchronize,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, Native.FileFlagBackupSemantics, IntPtr.Zero);

    /// <summary>
    /// Serial do volume quando a pasta está num disco local NTFS (onde o file ID identifica o arquivo e há
    /// hardlinks); null nos outros casos, que seguem com a listagem do .NET.
    /// </summary>
    public static uint? LocalNtfsVolume(string directory)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // Caminhos UNC (rede) e já prefixados ficam com o .NET.
        string? root = Path.GetPathRoot(full);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || root is not { Length: 3 } ||
            Native.GetDriveTypeW(root) is not (Native.DriveFixed or Native.DriveRemovable))
        {
            return null;
        }

        using var handle = OpenDirectory(full);
        if (handle.IsInvalid)
        {
            return null;
        }

        char* fileSystem = stackalloc char[32];
        if (!Native.GetVolumeInformationByHandleW(handle, null, 0, out uint serial, out _, out _, fileSystem, 32))
        {
            return null;
        }

        return new ReadOnlySpan<char>(fileSystem, 32).IndexOf('\0') is var end and >= 0 &&
               new ReadOnlySpan<char>(fileSystem, end).SequenceEqual("NTFS")
            ? serial
            : null;
    }

    private static class Native
    {
        public const uint FileListDirectory = 0x0001;
        public const uint Synchronize = 0x00100000;
        public const uint FileFlagBackupSemantics = 0x02000000;
        public const int FileIdBothDirectoryInfo = 10;
        public const int ErrorFileNotFound = 2;
        public const int ErrorPathNotFound = 3;
        public const int ErrorAccessDenied = 5;
        public const int ErrorNoMoreFiles = 18;
        public const uint DriveRemovable = 2;
        public const uint DriveFixed = 3;

        /// <summary>Deslocamento de FileName em FILE_ID_BOTH_DIR_INFO.</summary>
        public const int FileNameOffset = 104;

        /// <summary>FILE_ID_BOTH_DIR_INFO (só os campos usados).</summary>
        [StructLayout(LayoutKind.Explicit)]
        public struct FileIdBothDirInfo
        {
            [FieldOffset(0)] public uint NextEntryOffset;
            [FieldOffset(24)] public long LastWriteTime;
            [FieldOffset(40)] public long EndOfFile;
            [FieldOffset(56)] public uint FileAttributes;
            [FieldOffset(60)] public uint FileNameLength;
            [FieldOffset(96)] public long FileId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
            IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, byte* buffer, uint bufferSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, char* volumeName, uint volumeNameSize,
            out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemName,
            uint fileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern uint GetDriveTypeW(string rootPathName);
    }
}
