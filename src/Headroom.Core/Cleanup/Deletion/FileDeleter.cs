using Headroom.Core.Cleanup.Logging;
using Headroom.Core.Cleanup.Safety;
using Microsoft.VisualBasic.FileIO;

namespace Headroom.Core.Cleanup.Deletion;

/// <summary>Envia itens para a Lixeira (abstração para permitir testes sem tocar na Lixeira real).</summary>
public interface IRecycler
{
    void RecycleFile(string path);

    void RecycleDirectory(string path);
}

/// <summary>Lixeira do Windows via Microsoft.VisualBasic.FileIO.FileSystem (RecycleOption.SendToRecycleBin).</summary>
public sealed class ShellRecycler : IRecycler
{
    public void RecycleFile(string path) =>
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);

    public void RecycleDirectory(string path) =>
        FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
}

public sealed class DeletionOptions
{
    public DeletionMode Mode { get; init; } = DeletionMode.RecycleBin;

    /// <summary>Pastas onde é permitido apagar (null = exclusão manual: vale só a lista negra).</summary>
    public IReadOnlyList<AllowedRoot>? AllowedRoots { get; init; }

    /// <summary>Libera histórico/cookies/sessões de navegadores (regras opcionais).</summary>
    public bool AllowBrowserPrivacyData { get; init; }

    /// <summary>Rótulo usado no log (ex.: nome da regra).</summary>
    public string? LogLabel { get; init; }
}

/// <summary>
/// Executa exclusões com segurança: passa tudo pelo <see cref="DeletionGuard"/>, nunca entra em
/// junctions/links, pula arquivos em uso (sem travar e sem diálogos), registra cada item no log
/// e devolve um resumo com bytes liberados e itens pulados.
/// </summary>
public sealed class FileDeleter
{
    private readonly DeletionGuard _guard;
    private readonly IRecycler _recycler;
    private readonly CleanupLog _log;

    public FileDeleter(DeletionGuard? guard = null, IRecycler? recycler = null, CleanupLog? log = null)
    {
        _guard = guard ?? DeletionGuard.Default;
        _recycler = recycler ?? new ShellRecycler();
        _log = log ?? CleanupLog.Null();
    }

    /// <summary>Executa em uma thread STA dedicada (a Lixeira usa a API de Shell do Windows).</summary>
    public Task<DeletionResult> DeleteAsync(IReadOnlyList<DeletionTarget> targets, DeletionOptions options,
        IProgress<DeletionProgress>? progress = null, CancellationToken cancellationToken = default) =>
        StaThread.RunAsync(() => Delete(targets, options, progress, cancellationToken));

    public DeletionResult Delete(IReadOnlyList<DeletionTarget> targets, DeletionOptions options,
        IProgress<DeletionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new DeletionResult();
        if (!string.IsNullOrEmpty(options.LogLabel))
        {
            _log.Section($"{options.LogLabel} ({(options.Mode == DeletionMode.RecycleBin ? "Lixeira" : "exclusão permanente")})");
        }

        for (int i = 0; i < targets.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                _log.Info("Cancelado pelo usuário.");
                break;
            }

            var target = targets[i];
            progress?.Report(new DeletionProgress(i, targets.Count, target.Path, result.BytesDeleted));
            DeleteTarget(target.Path, options, result, cancellationToken);
        }

        progress?.Report(new DeletionProgress(targets.Count, targets.Count, string.Empty, result.BytesDeleted));
        return result;
    }

    private void DeleteTarget(string path, DeletionOptions options, DeletionResult result, CancellationToken ct)
    {
        var check = _guard.Check(path, options.AllowedRoots, options.AllowBrowserPrivacyData);
        if (check.IsProtected)
        {
            result.AddProtected(path, check.Reason);
            _log.Item("BLOQUEADO", 0, path, check.Reason);
            return;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            result.AddNotFound();
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.AddDenied(path);
            _log.Item("SEM PERMISSÃO", 0, path, ex.Message);
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            RemoveLinkOnly(path, (attributes & FileAttributes.Directory) != 0, result);
            return;
        }

        if (options.Mode == DeletionMode.RecycleBin && !FileProbe.SupportsRecycleBin(path))
        {
            result.AddNoRecycleBin(path);
            _log.Item("SEM LIXEIRA", 0, path, "Este disco não tem Lixeira; use a exclusão permanente.");
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            DeleteDirectory(path, options, result, ct);
        }
        else
        {
            DeleteFile(path, SafeLength(path), options.Mode, result);
        }
    }

    private void DeleteFile(string path, long size, DeletionMode mode, DeletionResult result)
    {
        switch (FileProbe.Probe(path))
        {
            case ProbeResult.InUse:
                result.AddInUse(path);
                _log.Item("EM USO (PULADO)", size, path);
                return;
            case ProbeResult.AccessDenied:
                result.AddDenied(path);
                _log.Item("SEM PERMISSÃO", size, path);
                return;
            case ProbeResult.NotFound:
                result.AddNotFound();
                return;
        }

        try
        {
            if (mode == DeletionMode.RecycleBin)
            {
                _recycler.RecycleFile(path);
                _log.Item("LIXEIRA", size, path);
            }
            else
            {
                ClearReadOnly(path);
                File.Delete(path);
                _log.Item("EXCLUÍDO", size, path);
            }

            result.AddDeletedFile(size);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            result.AddInUse(path);
            _log.Item("EM USO (PULADO)", size, path);
        }
        catch (UnauthorizedAccessException ex)
        {
            result.AddDenied(path);
            _log.Item("SEM PERMISSÃO", size, path, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            result.AddError(path, ex.Message);
            _log.Item("ERRO", size, path, ex.Message);
        }
    }

    private void DeleteDirectory(string path, DeletionOptions options, DeletionResult result, CancellationToken ct)
    {
        if (options.Mode == DeletionMode.RecycleBin)
        {
            // Se nada dentro da pasta impede, envia a pasta inteira de uma vez (muito mais rápido).
            var survey = Survey(path, options, ct);
            if (survey.Clean)
            {
                try
                {
                    _recycler.RecycleDirectory(path);
                    result.AddDeletedDirectory(survey.Bytes, survey.Files);
                    _log.Item("LIXEIRA (PASTA)", survey.Bytes, path, $"{survey.Files} arquivo(s)");
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    // Algo mudou entre a verificação e a exclusão: cai para o modo item a item.
                    _log.Info($"Pasta não pôde ir inteira para a Lixeira ({ex.Message}); tentando item a item: {path}");
                }
            }
        }

        DeleteDirectoryItemByItem(path, options, result, ct);
    }

    private void DeleteDirectoryItemByItem(string path, DeletionOptions options, DeletionResult result, CancellationToken ct)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", EnumOptions).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.AddDenied(path);
            _log.Item("SEM PERMISSÃO", 0, path, ex.Message);
            return;
        }

        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                return;
            }

            var check = _guard.Check(entry.FullName, options.AllowedRoots, options.AllowBrowserPrivacyData);
            if (check.IsProtected)
            {
                result.AddProtected(entry.FullName, check.Reason);
                _log.Item("BLOQUEADO", 0, entry.FullName, check.Reason);
                continue;
            }

            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                RemoveLinkOnly(entry.FullName, entry is DirectoryInfo, result);
                continue;
            }

            if (entry is DirectoryInfo)
            {
                DeleteDirectory(entry.FullName, options, result, ct);
            }
            else
            {
                DeleteFile(entry.FullName, ((FileInfo)entry).Length, options.Mode, result);
            }
        }

        // Remove a pasta se ficou vazia (pasta vazia não ocupa espaço; não precisa ir para a Lixeira).
        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                var info = new DirectoryInfo(path);
                if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    info.Attributes &= ~FileAttributes.ReadOnly;
                }

                info.Delete(false);
                result.AddDeletedEmptyDirectory();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A pasta continua existindo (ex.: um arquivo em uso ficou dentro): sem problema.
        }
    }

    /// <summary>Percorre a pasta (sem seguir links) para saber se dá para enviá-la inteira à Lixeira.</summary>
    private DirectorySurvey Survey(string root, DeletionOptions options, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested)
            {
                return new DirectorySurvey(false, bytes, files);
            }

            string dir = stack.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", EnumOptions).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new DirectorySurvey(false, bytes, files);
            }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    _guard.Check(entry.FullName, options.AllowedRoots, options.AllowBrowserPrivacyData).IsProtected)
                {
                    return new DirectorySurvey(false, bytes, files);
                }

                if (entry is DirectoryInfo)
                {
                    stack.Push(entry.FullName);
                    continue;
                }

                if (FileProbe.Probe(entry.FullName) != ProbeResult.Deletable)
                {
                    return new DirectorySurvey(false, bytes, files);
                }

                bytes += ((FileInfo)entry).Length;
                files++;
            }
        }

        return new DirectorySurvey(true, bytes, files);
    }

    private void RemoveLinkOnly(string path, bool isDirectory, DeletionResult result)
    {
        // Links nunca são seguidos: o destino fica intacto; só o atalho do sistema de arquivos sai.
        try
        {
            if (isDirectory)
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }

            result.AddLink(path);
            _log.Item("LINK REMOVIDO", 0, path, "Destino não foi tocado");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.AddLink(path);
            _log.Item("LINK MANTIDO", 0, path, ex.Message);
        }
    }

    private static readonly EnumerationOptions EnumOptions = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private static long SafeLength(string path)
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

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private readonly record struct DirectorySurvey(bool Clean, long Bytes, int Files);
}

/// <summary>Executa trabalho em uma thread STA dedicada (exigência de algumas APIs de Shell).</summary>
public static class StaThread
{
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Headroom STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
