using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Headroom.Core.SystemTools;

public sealed record CommandResult(int ExitCode, string Output, string Error, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : Output + Environment.NewLine + Error;
}

/// <summary>
/// Executa programas externos (dism, powercfg, docker, npm...) sem janela, capturando a saída com a
/// codificação certa, com tempo-limite e cancelamento (o processo é encerrado se cancelado).
/// </summary>
public static class CommandRunner
{
    static CommandRunner()
    {
        // Habilita as páginas de código do console (850/437) usadas por ferramentas nativas.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Codificação OEM do console (ex.: 850 em pt-BR) usada por dism, powercfg, ipconfig...</summary>
    public static Encoding ConsoleEncoding
    {
        get
        {
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (ArgumentException)
            {
                return Encoding.UTF8;
            }
        }
    }

    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        IProgress<string>? outputLines = null,
        Encoding? encoding = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? ConsoleEncoding,
            StandardErrorEncoding = encoding ?? ConsoleEncoding,
        };

        var output = new StringBuilder();
        var error = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            string line = e.Data.Replace("\0", string.Empty);
            lock (output)
            {
                output.AppendLine(line);
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                outputLines?.Report(line);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (error)
                {
                    error.AppendLine(e.Data.Replace("\0", string.Empty));
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new CommandResult(-1, string.Empty, $"Não foi possível executar \"{fileName}\": {ex.Message}", false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromHours(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (!timedOut)
            {
                throw;
            }
        }

        // Garante que os eventos de saída terminaram de chegar.
        if (!timedOut)
        {
            process.WaitForExit();
        }

        string outText, errText;
        lock (output)
        {
            outText = output.ToString();
        }

        lock (error)
        {
            errText = error.ToString();
        }

        return new CommandResult(timedOut ? -1 : process.ExitCode, outText, errText, timedOut);
    }

    /// <summary>Executa um script do Windows PowerShell 5.1 com saída UTF-8.</summary>
    public static Task<CommandResult> RunPowerShellAsync(string script, TimeSpan? timeout = null,
        IProgress<string>? outputLines = null, CancellationToken cancellationToken = default)
    {
        string full = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + script;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync(PowerShellPath, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            timeout, outputLines, Encoding.UTF8, cancellationToken);
    }

    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    /// <summary>Procura um executável no PATH (com extensões do PATHEXT).</summary>
    public static string? FindOnPath(string name)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in paths)
        {
            try
            {
                string candidate = Path.Combine(dir.Trim().Trim('"'), name);
                if (Path.HasExtension(name) && File.Exists(candidate))
                {
                    return candidate;
                }

                foreach (var ext in extensions)
                {
                    if (File.Exists(candidate + ext))
                    {
                        return candidate + ext;
                    }
                }
            }
            catch (ArgumentException)
            {
                // Entrada inválida no PATH: ignora.
            }
        }

        return null;
    }

    /// <summary>
    /// Monta a linha de execução para .cmd/.bat (precisam passar pelo cmd.exe) ou .exe diretamente.
    /// </summary>
    public static (string File, string Arguments) Wrap(string executable, string arguments)
    {
        string ext = Path.GetExtension(executable);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return (SystemTool("cmd.exe"), $"/d /c \"\"{executable}\" {arguments}\"");
        }

        return (executable, arguments);
    }
}
