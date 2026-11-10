using System.Diagnostics;
using System.ServiceProcess;
using Headroom.Core.Formatting;

namespace Headroom.Core.SystemTools;

/// <summary>Para e reinicia serviços do Windows (ex.: wuauserv antes de limpar o cache do Windows Update).</summary>
public static class ServiceHelper
{
    /// <summary>Para os serviços e devolve os que estavam rodando (para reiniciar depois).</summary>
    public static async Task<List<string>> StopAsync(IEnumerable<string> serviceNames, CancellationToken ct = default)
    {
        var stopped = new List<string>();
        foreach (var name in serviceNames)
        {
            try
            {
                using var service = new ServiceController(name);
                if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                {
                    continue;
                }

                if (!service.CanStop)
                {
                    continue;
                }

                service.Stop();
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)), ct)
                    .ConfigureAwait(false);
                stopped.Add(name);
            }
            catch (InvalidOperationException)
            {
                // Serviço inexistente ou sem permissão: segue em frente.
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                stopped.Add(name);
            }
        }

        return stopped;
    }

    public static async Task StartAsync(IEnumerable<string> serviceNames)
    {
        foreach (var name in serviceNames)
        {
            try
            {
                using var service = new ServiceController(name);
                if (service.Status == ServiceControllerStatus.Stopped)
                {
                    service.Start();
                    await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(45)))
                        .ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ServiceProcess.TimeoutException)
            {
            }
        }
    }

    public static ServiceControllerStatus? GetStatus(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Processo em execução que precisa ser fechado antes de uma limpeza.</summary>
public sealed record RunningProcessInfo(string ExecutableName, int Count, string? Title);

/// <summary>Detecta e fecha processos (ex.: navegador aberto antes de limpar o histórico).</summary>
public static class ProcessHelper
{
    public static IReadOnlyList<RunningProcessInfo> FindRunning(IEnumerable<string> executableNames)
    {
        var result = new List<RunningProcessInfo>();
        foreach (var exe in executableNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            try
            {
                if (processes.Length > 0)
                {
                    string? title = processes.Select(p => SafeTitle(p)).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                    result.Add(new RunningProcessInfo(exe, processes.Length, title));
                }
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }

        return result;
    }

    /// <summary>Pede para fechar (como clicar no X) e, se não fechar a tempo, encerra.</summary>
    public static async Task CloseAsync(IEnumerable<string> executableNames, TimeSpan gracePeriod)
    {
        foreach (var exe in executableNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            foreach (var process in processes)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        process.CloseMainWindow();
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }

            var deadline = DateTime.UtcNow + gracePeriod;
            foreach (var process in processes)
            {
                try
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        using var cts = new CancellationTokenSource(remaining);
                        try
                        {
                            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }

                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Processo já saiu ou pertence a outro usuário.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private static string? SafeTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Ponto de restauração do Windows (Checkpoint-Computer), antes de limpezas avançadas.</summary>
public static class RestorePointService
{
    public static async Task<(bool Success, string Message)> CreateAsync(string description, CancellationToken ct = default)
    {
        string script =
            "try { Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -ErrorAction SilentlyContinue; " +
            $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; " +
            "'OK' } catch { 'ERRO: ' + $_.Exception.Message }";
        var result = await CommandRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(5), cancellationToken: ct)
            .ConfigureAwait(false);
        string output = result.Output.Trim();
        if (output.EndsWith("OK", StringComparison.Ordinal))
        {
            return (true, "Ponto de restauração criado.");
        }

        // O Windows só permite um ponto de restauração a cada 24 h por padrão (aviso, não erro).
        if (result.Combined.Contains("1440", StringComparison.Ordinal) ||
            result.Combined.Contains("24 hours", StringComparison.OrdinalIgnoreCase) ||
            result.Combined.Contains("24 horas", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "Já existe um ponto de restauração criado nas últimas 24 horas (o Windows não cria outro).");
        }

        return (false, "Não foi possível criar o ponto de restauração: " +
                       (string.IsNullOrWhiteSpace(output) ? result.Error.Trim() : output.Replace("ERRO: ", string.Empty)));
    }
}

/// <summary>Medidas de disco usadas em regras de comando (bytes liberados = diferença de espaço livre).</summary>
public static class DiskMeasure
{
    public static long FreeSpace(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "C:\\";
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static string SystemDrive => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";

    /// <summary>Tamanho de uma pasta (sem seguir links); 0 se não existir.</summary>
    public static long DirectorySize(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return File.Exists(path) ? SafeFileLength(path) : 0;
        }

        long total = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                total += file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }

    public static long SafeFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static string Describe(long bytes) => ByteSize.Format(bytes);
}
