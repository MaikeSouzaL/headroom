using Headroom.Core.Cleanup.Logging;
using Headroom.Core.Cleanup.Rules;
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
/// <remarks>
/// Bytes liberados contam cada arquivo uma vez por chamada de <see cref="Delete"/>, como a análise: apagar vários
/// hardlinks do mesmo arquivo (uv, pnpm, conda) não soma o tamanho de novo. Em NTFS local o file ID vem da listagem
/// da pasta (sem abrir o arquivo) ou, para um arquivo indicado diretamente, do próprio teste de exclusão.
/// </remarks>
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
        var run = new Run(options, result, cancellationToken);
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
            DeleteTarget(target.Path, run);
        }

        progress?.Report(new DeletionProgress(targets.Count, targets.Count, string.Empty, result.BytesDeleted));
        return result;
    }

    private void DeleteTarget(string path, Run run)
    {
        var options = run.Options;
        var result = run.Result;
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
            string directory;
            try
            {
                directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                result.AddError(path, ex.Message);
                _log.Item("ERRO", 0, path, ex.Message);
                return;
            }

            // Links nunca são seguidos, então tudo dentro da pasta fica no mesmo volume.
            DeleteDirectory(directory, DirectoryReader.LocalNtfsVolume(directory), run);
        }
        else
        {
            DeleteFile(path, SafeLength(path), key: null, run);
        }
    }

    /// <param name="key">Volume e file ID vindos da listagem da pasta; null = descobrir pelo teste de exclusão.</param>
    private void DeleteFile(string path, long size, FileKey? key, Run run)
    {
        var mode = run.Options.Mode;
        var result = run.Result;
        FileIdentity identity = default;
        var probe = key is null ? FileProbe.Probe(path, out identity) : FileProbe.Probe(path);
        switch (probe)
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
            }
            else
            {
                ClearReadOnly(path);
                File.Delete(path);
            }

            long freed = run.Count(size, key ?? run.KeyOf(path, identity));
            _log.Item(mode == DeletionMode.RecycleBin ? "LIXEIRA" : "EXCLUÍDO", freed, path,
                freed == size ? null : "Outro hardlink deste arquivo já foi contado");
            result.AddDeletedFile(freed);
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

    /// <param name="path">Pasta (caminho completo e normalizado).</param>
    /// <param name="volume">Serial do volume NTFS local (file IDs na listagem) ou null.</param>
    private void DeleteDirectory(string path, uint? volume, Run run)
    {
        var options = run.Options;
        var result = run.Result;
        if (options.Mode == DeletionMode.RecycleBin)
        {
            // Se nada dentro da pasta impede, envia a pasta inteira de uma vez (muito mais rápido).
            var survey = FolderSurvey.Run(path, volume,
                p => _guard.Check(p, options.AllowedRoots, options.AllowBrowserPrivacyData).IsProtected,
                run.Freed, ContentWalker.ThreadsFor(path), run.Ct);
            if (survey.Clean)
            {
                try
                {
                    _recycler.RecycleDirectory(path);
                    run.Freed.UnionWith(survey.Seen);
                    int files = (int)Math.Min(int.MaxValue, survey.Files);
                    result.AddDeletedDirectory(survey.Bytes, files);
                    _log.Item("LIXEIRA (PASTA)", survey.Bytes, path, $"{files} arquivo(s)");
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    // Algo mudou entre a verificação e a exclusão: cai para o modo item a item.
                    _log.Info($"Pasta não pôde ir inteira para a Lixeira ({ex.Message}); tentando item a item: {path}");
                }
            }
        }

        DeleteDirectoryItemByItem(path, volume, run);
    }

    private void DeleteDirectoryItemByItem(string path, uint? volume, Run run)
    {
        var options = run.Options;
        var result = run.Result;
        List<FolderEntry> entries;
        try
        {
            // Sem cancelamento no meio da listagem: ele é conferido entre os itens, como antes.
            entries = FolderListing.List(path, volume, ref run.Buffer, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.AddDenied(path);
            _log.Item("SEM PERMISSÃO", 0, path, ex.Message);
            return;
        }

        foreach (var entry in entries)
        {
            if (run.Ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                return;
            }

            string full = Path.Join(path, entry.Name);
            var check = _guard.Check(full, options.AllowedRoots, options.AllowBrowserPrivacyData);
            if (check.IsProtected)
            {
                result.AddProtected(full, check.Reason);
                _log.Item("BLOQUEADO", 0, full, check.Reason);
                continue;
            }

            if (entry.IsLink)
            {
                RemoveLinkOnly(full, entry.IsDirectory, result);
                continue;
            }

            if (entry.IsDirectory)
            {
                DeleteDirectory(full, volume, run);
            }
            else
            {
                DeleteFile(full, entry.Length, volume is uint v ? new FileKey(v, entry.FileId) : FileKey.PerPath, run);
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

    /// <summary>Arquivo pela listagem da pasta: volume e file ID (file ID 0 = conta por caminho).</summary>
    private readonly record struct FileKey(uint Volume, long FileId)
    {
        /// <summary>Sem file ID (outro sistema de arquivos): o caminho conta o tamanho inteiro, como antes.</summary>
        public static readonly FileKey PerPath = new(0, 0);
    }

    /// <summary>Estado de uma chamada de <see cref="Delete"/>.</summary>
    private sealed class Run(DeletionOptions options, DeletionResult result, CancellationToken ct)
    {
        private readonly Dictionary<string, uint?> _ntfsByRoot = new(StringComparer.OrdinalIgnoreCase);

        public DeletionOptions Options { get; } = options;
        public DeletionResult Result { get; } = result;
        public CancellationToken Ct { get; } = ct;

        /// <summary>Arquivos (volume + file ID) já contados como liberados nesta exclusão.</summary>
        public HardLinkSet Freed { get; } = new();

        /// <summary>Buffer da listagem nativa (a exclusão item a item roda numa thread só).</summary>
        public byte[]? Buffer;

        /// <summary>Bytes que apagar este caminho libera: zero se outro hardlink do mesmo arquivo já foi contado.</summary>
        public long Count(long size, FileKey key) =>
            key.FileId == 0 || Freed.TryAdd(key.Volume, key.FileId) ? size : 0;

        /// <summary>Chave de um arquivo indicado diretamente, pelo handle do teste de exclusão (só em NTFS local).</summary>
        public FileKey KeyOf(string path, FileIdentity identity)
        {
            if (identity.FileIndex == 0)
            {
                return FileKey.PerPath;
            }

            string? root;
            try
            {
                root = Path.GetPathRoot(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return FileKey.PerPath;
            }

            if (string.IsNullOrEmpty(root))
            {
                return FileKey.PerPath;
            }

            if (!_ntfsByRoot.TryGetValue(root, out uint? volume))
            {
                volume = DirectoryReader.LocalNtfsVolume(root);
                _ntfsByRoot[root] = volume;
            }

            // Serial diferente: o arquivo está atrás de um ponto de montagem de outro volume; conta por caminho.
            return volume == identity.VolumeSerial ? new FileKey(identity.VolumeSerial, identity.FileIndex) : FileKey.PerPath;
        }
    }
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
