using System.Text;

namespace Headroom.Core.Scanning;

/// <summary>
/// Nó da árvore em memória (pasta ou arquivo). Projetado para ser compacto, pois um scan pode
/// gerar milhões de nós: o caminho completo não é armazenado (é calculado subindo pelos pais) e
/// arquivos não carregam campos que só pastas usam (lista de filhos, contagem de arquivos).
/// </summary>
public abstract class ScanNode
{
    private protected static readonly ScanNode[] NoChildren = Array.Empty<ScanNode>();

    private long _lastWriteUtcTicks;

    private protected ScanNode(string name, DirectoryNode? parent, long size, long lastWriteUtcTicks)
    {
        Name = name;
        ParentDirectory = parent;
        Size = size;
        _lastWriteUtcTicks = lastWriteUtcTicks;
    }

    /// <summary>Cria um nó raiz (pasta) para um caminho completo.</summary>
    public static ScanNode CreateRoot(string fullPath, DateTime lastWriteUtc) =>
        new DirectoryNode(fullPath, null, lastWriteUtc.Ticks);

    internal static DirectoryNode CreateDirectory(string name, DirectoryNode? parent, long lastWriteUtcTicks) =>
        new(name, parent, lastWriteUtcTicks);

    internal static FileNode CreateFile(string name, DirectoryNode? parent, long size, long lastWriteUtcTicks,
        FileCategory category) =>
        new(name, parent, size, lastWriteUtcTicks, category);

    /// <summary>Nome do arquivo/pasta. Na raiz, é o caminho completo (ex.: "C:\").</summary>
    public string Name { get; }

    public ScanNode? Parent => ParentDirectory;

    internal DirectoryNode? ParentDirectory { get; set; }

    public abstract bool IsDirectory { get; }

    /// <summary>Tamanho em bytes (recursivo para pastas).</summary>
    public long Size { get; internal set; }

    /// <summary>Quantidade de arquivos (recursiva para pastas; 1 para arquivos).</summary>
    public abstract long FileCount { get; }

    public abstract FileCategory Category { get; }

    public abstract IReadOnlyList<ScanNode> Children { get; }

    public bool HasChildren => Children.Count > 0;

    /// <summary>Data de modificação mais recente (para pastas, a mais recente da subárvore).</summary>
    public DateTime LastModifiedUtc => new(_lastWriteUtcTicks, DateTimeKind.Utc);

    public DateTime LastModified => LastModifiedUtc.ToLocalTime();

    internal long LastWriteUtcTicks
    {
        get => _lastWriteUtcTicks;
        set => _lastWriteUtcTicks = value;
    }

    public bool IsRoot => ParentDirectory is null;

    public string Extension => IsDirectory ? string.Empty : CategoryClassifier.GetExtension(Name);

    /// <summary>Fração do tamanho em relação à pasta-pai (0..1).</summary>
    public double FractionOfParent =>
        ParentDirectory is null || ParentDirectory.Size <= 0 ? 1 : Size / (double)ParentDirectory.Size;

    public int Depth
    {
        get
        {
            int depth = 0;
            for (var n = ParentDirectory; n is not null; n = n.ParentDirectory)
            {
                depth++;
            }

            return depth;
        }
    }

    /// <summary>Caminho completo, calculado subindo pelos pais.</summary>
    public string FullPath
    {
        get
        {
            if (ParentDirectory is null)
            {
                return Name;
            }

            var parts = new List<string>(16);
            ScanNode node = this;
            while (node.ParentDirectory is not null)
            {
                parts.Add(node.Name);
                node = node.ParentDirectory;
            }

            var sb = new StringBuilder(256);
            sb.Append(node.Name.TrimEnd('\\', '/'));
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                sb.Append('\\').Append(parts[i]);
            }

            return sb.ToString();
        }
    }

    /// <summary>Pasta que contém o item.</summary>
    public string ParentPath => ParentDirectory?.FullPath ?? Path.GetDirectoryName(Name) ?? string.Empty;

    public ScanNode? FindChild(string name)
    {
        foreach (var child in Children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    public IEnumerable<ScanNode> Ancestors()
    {
        for (var n = ParentDirectory; n is not null; n = n.ParentDirectory)
        {
            yield return n;
        }
    }

    public bool IsAncestorOf(ScanNode other)
    {
        for (var n = other.ParentDirectory; n is not null; n = n.ParentDirectory)
        {
            if (ReferenceEquals(n, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Percorre todos os arquivos da subárvore (iterativo, sem recursão).</summary>
    public IEnumerable<ScanNode> EnumerateFiles()
    {
        if (!IsDirectory)
        {
            yield return this;
            yield break;
        }

        var stack = new Stack<ScanNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            foreach (var child in stack.Pop().Children)
            {
                if (child.IsDirectory)
                {
                    stack.Push(child);
                }
                else
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>Percorre todas as pastas da subárvore (inclui a própria).</summary>
    public IEnumerable<ScanNode> EnumerateDirectories()
    {
        if (!IsDirectory)
        {
            yield break;
        }

        var stack = new Stack<ScanNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            yield return dir;
            foreach (var child in dir.Children)
            {
                if (child.IsDirectory)
                {
                    stack.Push(child);
                }
            }
        }
    }

    /// <summary>
    /// Remove este nó da árvore e desconta tamanho/arquivos de todos os ancestrais.
    /// Usado depois que o usuário apaga um item pela interface.
    /// </summary>
    public bool Detach()
    {
        var parent = ParentDirectory;
        if (parent is null || !parent.RemoveChild(this))
        {
            return false;
        }

        for (var n = parent; n is not null; n = n.ParentDirectory)
        {
            n.Size -= Size;
            n.AddFileCount(-FileCount);
        }

        ParentDirectory = null;
        return true;
    }

    public override string ToString() => $"{Name} ({Size} bytes)";
}

/// <summary>Arquivo: só guarda a categoria além dos campos comuns.</summary>
internal sealed class FileNode : ScanNode
{
    private readonly FileCategory _category;

    internal FileNode(string name, DirectoryNode? parent, long size, long lastWriteUtcTicks, FileCategory category)
        : base(name, parent, size, lastWriteUtcTicks)
    {
        _category = category;
    }

    public override bool IsDirectory => false;
    public override long FileCount => 1;
    public override FileCategory Category => _category;
    public override IReadOnlyList<ScanNode> Children => NoChildren;
}

/// <summary>Pasta: lista de filhos (em construção ou já ordenada) e contagem recursiva de arquivos.</summary>
internal sealed class DirectoryNode : ScanNode
{
    private List<ScanNode>? _building;
    private ScanNode[] _children = NoChildren;
    private long _fileCount;

    internal DirectoryNode(string name, DirectoryNode? parent, long lastWriteUtcTicks)
        : base(name, parent, 0, lastWriteUtcTicks)
    {
    }

    public override bool IsDirectory => true;
    public override long FileCount => _fileCount;
    public override FileCategory Category => FileCategory.Other;
    public override IReadOnlyList<ScanNode> Children => (IReadOnlyList<ScanNode>?)_building ?? _children;

    /// <summary>Adiciona um filho durante o scan (cada pasta é lida por uma única thread).</summary>
    internal void AddChild(ScanNode child) => (_building ??= new List<ScanNode>(8)).Add(child);

    internal void AddFileCount(long delta) => _fileCount += delta;

    /// <summary>Agrega totais a partir dos filhos já finalizados e congela a lista ordenada por tamanho.</summary>
    internal void Seal()
    {
        long size = 0, files = 0, maxTicks = LastWriteUtcTicks;
        if (_building is { Count: > 0 } list)
        {
            foreach (var child in list)
            {
                size += child.Size;
                files += child.FileCount;
                if (child.LastWriteUtcTicks > maxTicks)
                {
                    maxTicks = child.LastWriteUtcTicks;
                }
            }

            var array = list.ToArray();
            Array.Sort(array, static (a, b) => b.Size.CompareTo(a.Size));
            _children = array;
        }

        _building = null;
        Size = size;
        _fileCount = files;
        LastWriteUtcTicks = maxTicks;
    }

    internal bool RemoveChild(ScanNode child)
    {
        if (_building is not null)
        {
            return _building.Remove(child);
        }

        int index = Array.IndexOf(_children, child);
        if (index < 0)
        {
            return false;
        }

        if (_children.Length == 1)
        {
            _children = NoChildren;
            return true;
        }

        var copy = new ScanNode[_children.Length - 1];
        Array.Copy(_children, 0, copy, 0, index);
        Array.Copy(_children, index + 1, copy, index, _children.Length - index - 1);
        _children = copy;
        return true;
    }
}
