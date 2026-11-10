namespace Headroom.Core.Cleanup.Deletion;

/// <summary>Como apagar: mover para a Lixeira (padrão) ou excluir permanentemente.</summary>
public enum DeletionMode
{
    RecycleBin,
    Permanent,
}

/// <summary>Item a apagar (arquivo ou pasta inteira).</summary>
public sealed record DeletionTarget(string Path, bool IsDirectory, long EstimatedSize = 0);

public sealed record DeletionProgress(int ProcessedItems, int TotalItems, string CurrentPath, long BytesDeleted);

/// <summary>Resultado consolidado de uma exclusão: o que saiu, o que foi pulado e por quê.</summary>
public sealed class DeletionResult
{
    private const int MaxMessages = 300;
    private readonly List<string> _messages = new();

    /// <summary>
    /// Espaço que saiu: cada arquivo conta uma vez, mesmo quando vários hardlinks dele foram apagados (como na análise).
    /// Se um link do arquivo ficar fora da limpeza, o espaço só volta quando ele também sair.
    /// </summary>
    public long BytesDeleted { get; private set; }

    /// <summary>Caminhos de arquivo apagados (cada hardlink é um caminho).</summary>
    public int FilesDeleted { get; private set; }
    public int DirectoriesDeleted { get; private set; }
    public int SkippedInUse { get; private set; }
    public int SkippedAccessDenied { get; private set; }
    public int SkippedProtected { get; private set; }
    public int SkippedNoRecycleBin { get; private set; }
    public int SkippedLinks { get; private set; }
    public int NotFound { get; private set; }
    public int Errors { get; private set; }
    public bool Cancelled { get; internal set; }

    public int TotalSkipped => SkippedInUse + SkippedAccessDenied + SkippedProtected + SkippedNoRecycleBin + SkippedLinks;
    public IReadOnlyList<string> Messages => _messages;

    internal void AddDeletedFile(long size)
    {
        FilesDeleted++;
        BytesDeleted += size;
    }

    internal void AddDeletedDirectory(long size, int files)
    {
        DirectoriesDeleted++;
        FilesDeleted += files;
        BytesDeleted += size;
    }

    internal void AddDeletedEmptyDirectory() => DirectoriesDeleted++;

    internal void AddInUse(string path) { SkippedInUse++; AddMessage($"Em uso: {path}"); }
    internal void AddDenied(string path) { SkippedAccessDenied++; AddMessage($"Sem permissão: {path}"); }
    internal void AddProtected(string path, string reason) { SkippedProtected++; AddMessage($"Protegido: {path} ({reason})"); }
    internal void AddNoRecycleBin(string path) { SkippedNoRecycleBin++; AddMessage($"Disco sem Lixeira: {path}"); }
    internal void AddLink(string path) { SkippedLinks++; AddMessage($"Link/junction não seguido: {path}"); }
    internal void AddNotFound() => NotFound++;
    internal void AddError(string path, string message) { Errors++; AddMessage($"Erro em {path}: {message}"); }

    private void AddMessage(string message)
    {
        if (_messages.Count < MaxMessages)
        {
            _messages.Add(message);
        }
    }

    /// <summary>Soma outro resultado a este (usado para consolidar várias regras).</summary>
    public void Merge(DeletionResult other)
    {
        BytesDeleted += other.BytesDeleted;
        FilesDeleted += other.FilesDeleted;
        DirectoriesDeleted += other.DirectoriesDeleted;
        SkippedInUse += other.SkippedInUse;
        SkippedAccessDenied += other.SkippedAccessDenied;
        SkippedProtected += other.SkippedProtected;
        SkippedNoRecycleBin += other.SkippedNoRecycleBin;
        SkippedLinks += other.SkippedLinks;
        NotFound += other.NotFound;
        Errors += other.Errors;
        Cancelled |= other.Cancelled;
        foreach (var message in other._messages)
        {
            AddMessage(message);
        }
    }
}
