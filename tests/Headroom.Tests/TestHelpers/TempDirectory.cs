using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Headroom.Tests.TestHelpers;

/// <summary>Pasta temporária única por teste, apagada no Dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeadroomTests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Cria um arquivo com o tamanho exato (conteúdo pseudo-aleatório determinístico).</summary>
    public string CreateFile(string relativePath, long size, int seed = 0, DateTime? lastWriteUtc = null)
    {
        string full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var random = new Random(seed == 0 ? relativePath.GetHashCode() : seed);
        var buffer = new byte[(int)Math.Min(size, 1024 * 1024)];
        using (var stream = File.Create(full))
        {
            long remaining = size;
            while (remaining > 0)
            {
                random.NextBytes(buffer);
                int chunk = (int)Math.Min(remaining, buffer.Length);
                stream.Write(buffer, 0, chunk);
                remaining -= chunk;
            }
        }

        if (lastWriteUtc is not null)
        {
            File.SetLastWriteTimeUtc(full, lastWriteUtc.Value);
        }

        return full;
    }

    public string CreateFileWithContent(string relativePath, byte[] content)
    {
        string full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string CreateDirectory(string relativePath)
    {
        string full = Combine(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Cria uma junction (não exige administrador).</summary>
    public static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (!Directory.Exists(link))
        {
            throw new InvalidOperationException("Não foi possível criar a junction de teste: " + process.StandardError.ReadToEnd());
        }
    }

    public static void CreateHardLink(string link, string existing)
    {
        if (!CreateHardLinkW(link, existing, IntPtr.Zero))
        {
            throw new IOException("CreateHardLink falhou: " + Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    public void Dispose()
    {
        try
        {
            ForceDelete(Path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ForceDelete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // Junction: remove só o link, nunca o conteúdo do destino.
                Directory.Delete(entry.FullName, recursive: false);
                continue;
            }

            if (entry is DirectoryInfo dir)
            {
                ForceDelete(dir.FullName);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        Directory.Delete(directory, recursive: false);
    }
}
