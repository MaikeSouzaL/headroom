using System.Globalization;
using System.Text;
using Headroom.Core.Formatting;
using Headroom.Core.Settings;

namespace Headroom.Core.Cleanup.Logging;

/// <summary>
/// Log de tudo o que foi apagado (ou pulado) em %LOCALAPPDATA%\Headroom\logs.
/// Uma linha por item: hora | ação | tamanho | caminho | detalhe. Thread-safe.
/// </summary>
public sealed class CleanupLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();

    private CleanupLog(string? filePath)
    {
        FilePath = filePath;
        if (filePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            _writer = new StreamWriter(new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(true))
            {
                AutoFlush = true,
            };
        }
    }

    /// <summary>Caminho do arquivo de log (null quando o log está desativado).</summary>
    public string? FilePath { get; }

    /// <summary>Cria um novo arquivo de log com data/hora no nome.</summary>
    public static CleanupLog Create(string prefix = "limpeza", string? directory = null)
    {
        string dir = directory ?? AppPaths.LogsDirectory;
        string file = Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var log = new CleanupLog(file);
        log.WriteRaw($"Headroom — log de {prefix} iniciado em {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        log.WriteRaw($"Usuário: {Environment.UserName}  Computador: {Environment.MachineName}");
        log.WriteRaw(new string('-', 100));
        return log;
    }

    /// <summary>Log que não grava nada (usado quando o log não é desejado, ex.: testes).</summary>
    public static CleanupLog Null() => new(null);

    public void Section(string title)
    {
        WriteRaw(string.Empty);
        WriteRaw($"== {title} ==");
    }

    public void Item(string action, long size, string path, string? detail = null)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss} | {action,-16} | {ByteSize.Format(size),10} | {path}");
        if (!string.IsNullOrEmpty(detail))
        {
            line += " | " + detail;
        }

        WriteRaw(line);
    }

    public void Info(string message) => WriteRaw($"{DateTime.Now:HH:mm:ss} | {message}");

    public void WriteRaw(string line)
    {
        if (_writer is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Disco cheio, por exemplo: o log nunca deve derrubar a limpeza.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }
}
