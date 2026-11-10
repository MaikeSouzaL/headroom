using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using Headroom.Core.Cleanup.Rules;

namespace Headroom.Core.Cleanup.Deletion;

/// <summary>Uma entrada de pasta para a limpeza, sem criar FileInfo/DirectoryInfo por arquivo.</summary>
/// <param name="FileId">File ID do NTFS (0 = desconhecido: o arquivo conta por caminho).</param>
internal readonly record struct FolderEntry(string Name, FileAttributes Attributes, long Length, long FileId)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0;
}

/// <summary>Listagem rasa de uma pasta para a limpeza (links nunca são seguidos: aparecem como entradas).</summary>
internal static class FolderListing
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private static readonly DirectoryReader.Transform<FolderEntry> NativeEntry = (name, attributes, length, _, fileId) =>
        (attributes & FileAttributes.Directory) != 0
            ? new FolderEntry(name.ToString(), attributes, 0, 0)
            : new FolderEntry(name.ToString(), attributes, length, fileId);

    private static readonly FileSystemEnumerable<FolderEntry>.FindTransform ManagedEntry = (ref FileSystemEntry entry) =>
        new FolderEntry(entry.FileName.ToString(), entry.Attributes, entry.IsDirectory ? 0 : entry.Length, 0);

    /// <summary>
    /// Lista <paramref name="directory"/> (caminho completo e normalizado). Com <paramref name="volume"/> (NTFS local)
    /// traz o file ID de cada arquivo; sem ele, usa a listagem do .NET. Pasta que não pode ser lida vira
    /// <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/> ou <see cref="DirectoryNotFoundException"/>,
    /// como no <see cref="DirectoryInfo"/>.
    /// </summary>
    public static List<FolderEntry> List(string directory, uint? volume, ref byte[]? buffer, CancellationToken ct)
    {
        var list = new List<FolderEntry>();
        if (volume is null)
        {
            foreach (var entry in new FileSystemEnumerable<FolderEntry>(directory, ManagedEntry, Options))
            {
                list.Add(entry);
            }

            return list;
        }

        buffer ??= DirectoryReader.NewBuffer();
        if (!DirectoryReader.TryList(directory, buffer, NativeEntry, list, ct, out int error))
        {
            throw DirectoryReader.OpenFailure(directory, error);
        }

        return list;
    }
}

/// <summary>
/// Antes de mandar uma pasta inteira para a Lixeira, confere TUDO o que há dentro dela, como a exclusão item a item
/// faria: nenhum link/junction (nunca seguidos), nenhum item barrado pelo <see cref="Safety.DeletionGuard"/> e
/// nenhum arquivo em uso ou sem permissão (<see cref="FileProbe"/>). Qualquer um desses faz a pasta cair no modo item
/// a item, que pula só o que não pode sair.
/// </summary>
/// <remarks>
/// Em caches com milhões de arquivos (o <c>builds-v0</c> do uv chega a 22 milhões de caminhos e 6 milhões de pastas)
/// o custo é abrir cada pasta e cada arquivo. Aqui as pastas são distribuídas entre várias threads, cada uma listando
/// com o file ID (sem FileInfo por arquivo) e testando os arquivos da pasta que pegou. Nenhuma verificação foi
/// retirada: só deixaram de ser feitas uma de cada vez. A primeira que falha para todas as threads.
/// </remarks>
internal sealed class FolderSurvey
{
    private readonly object _lock = new();
    private readonly Stack<string> _work = new();
    private readonly uint? _volume;
    private readonly Func<string, bool> _isProtected;
    private readonly HardLinkSet _counted;
    private readonly HardLinkSet _seen = new();
    private readonly int _maxThreads;
    private readonly CancellationToken _ct;

    /// <summary>Pastas na pilha ou sendo listadas.</summary>
    private int _pending;

    /// <summary>Threads dentro do laço (a que chamou <see cref="Run"/> conta como uma).</summary>
    private int _threads = 1;

    private volatile bool _stop;
    private bool _clean = true;
    private long _bytes;
    private long _files;
    private ExceptionDispatchInfo? _error;

    private FolderSurvey(uint? volume, Func<string, bool> isProtected, HardLinkSet counted, int maxThreads, CancellationToken ct)
    {
        _volume = volume;
        _isProtected = isProtected;
        _counted = counted;
        _maxThreads = Math.Max(1, maxThreads);
        _ct = ct;
    }

    /// <summary>Resultado: se a pasta pode sair inteira, os bytes que ela libera e quantos arquivos (caminhos) tem.</summary>
    /// <param name="Seen">Arquivos contados nesta pasta (para somar ao conjunto da exclusão se ela sair).</param>
    internal readonly record struct Result(bool Clean, long Bytes, long Files, HardLinkSet Seen);

    /// <param name="root">Pasta (caminho completo e normalizado).</param>
    /// <param name="volume">Serial do volume NTFS local (file IDs na listagem) ou null.</param>
    /// <param name="isProtected">A mesma verificação do DeletionGuard que a exclusão item a item usa.</param>
    /// <param name="counted">Arquivos já contados nesta exclusão: os hardlinks deles valem zero aqui (só leitura).</param>
    public static Result Run(string root, uint? volume, Func<string, bool> isProtected, HardLinkSet counted, int maxThreads,
        CancellationToken ct)
    {
        var survey = new FolderSurvey(volume, isProtected, counted, maxThreads, ct);
        survey._work.Push(root);
        survey._pending = 1;
        survey.WorkerLoop();

        lock (survey._lock)
        {
            // Nenhuma thread auxiliar pode continuar abrindo arquivos depois do retorno.
            while (survey._threads > 0)
            {
                Monitor.Wait(survey._lock);
            }
        }

        survey._error?.Throw();
        bool clean = survey._clean && !ct.IsCancellationRequested;
        return new Result(clean, survey._bytes, survey._files, survey._seen);
    }

    private void WorkerLoop()
    {
        byte[]? buffer = null;
        var subdirectories = new List<string>();
        try
        {
            while (true)
            {
                string directory;
                lock (_lock)
                {
                    while (_work.Count == 0 && _pending > 0 && !_stop)
                    {
                        Monitor.Wait(_lock);
                    }

                    if (_stop || _pending == 0)
                    {
                        return;
                    }

                    directory = _work.Pop();
                }

                subdirectories.Clear();
                bool ok = Visit(directory, ref buffer, subdirectories);
                bool spawn;
                lock (_lock)
                {
                    if (ok)
                    {
                        // Ordem inversa: a primeira subpasta sai primeiro da pilha (percurso em profundidade).
                        for (int i = subdirectories.Count - 1; i >= 0; i--)
                        {
                            _work.Push(subdirectories[i]);
                        }

                        _pending += subdirectories.Count;
                    }
                    else
                    {
                        _clean = false;
                        _stop = true;
                    }

                    _pending--;
                    spawn = !_stop && _threads < _maxThreads && _work.Count > 1;
                    if (spawn)
                    {
                        _threads++;
                    }

                    Monitor.PulseAll(_lock);
                }

                if (spawn)
                {
                    StartHelper();
                }
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _error ??= ExceptionDispatchInfo.Capture(ex);
                _clean = false;
                _stop = true;
            }
        }
        finally
        {
            lock (_lock)
            {
                _threads--;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private void StartHelper()
    {
        try
        {
            new Thread(WorkerLoop) { IsBackground = true, Name = "Headroom.FolderSurvey" }.Start();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
        {
            lock (_lock)
            {
                _threads--;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>Confere uma pasta; false = algo dentro dela impede mandar a pasta inteira (ou cancelado).</summary>
    private bool Visit(string directory, ref byte[]? buffer, List<string> subdirectories)
    {
        List<FolderEntry> entries;
        try
        {
            entries = FolderListing.List(directory, _volume, ref buffer, _ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        long bytes = 0;
        long files = 0;
        foreach (var entry in entries)
        {
            if (_stop || _ct.IsCancellationRequested)
            {
                return false;
            }

            string full = Path.Join(directory, entry.Name);
            if (entry.IsLink || _isProtected(full))
            {
                return false;
            }

            if (entry.IsDirectory)
            {
                subdirectories.Add(full);
                continue;
            }

            if (FileProbe.Probe(full) != ProbeResult.Deletable)
            {
                return false;
            }

            // Outro hardlink do mesmo arquivo já contado (nesta pasta ou antes, nesta exclusão): não libera de novo.
            if (entry.FileId == 0 || _volume is not uint volume ||
                (!_counted.Contains(volume, entry.FileId) && _seen.TryAdd(volume, entry.FileId)))
            {
                bytes += entry.Length;
            }

            files++;
        }

        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _files, files);
        return true;
    }
}
