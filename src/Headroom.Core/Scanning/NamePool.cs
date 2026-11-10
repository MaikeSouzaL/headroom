namespace Headroom.Core.Scanning;

/// <summary>
/// Cache de strings de mapeamento direto (como um cache de CPU), usado por uma única thread.
/// Em discos com milhões de arquivos, nomes como "index.js", "package.json" ou "__init__.py" se
/// repetem centenas de milhares de vezes; reaproveitar a mesma instância economiza memória e
/// evita alocações (a busca é feita pelo span, sem criar string quando há acerto).
/// </summary>
internal sealed class NamePool
{
    private const int MaxPooledLength = 48;
    private readonly string?[] _slots = new string?[1 << 16];

    public string Get(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return string.Empty;
        }

        if (value.Length > MaxPooledLength)
        {
            return value.ToString();
        }

        int slot = string.GetHashCode(value) & (_slots.Length - 1);
        string? existing = _slots[slot];
        if (existing is not null && value.SequenceEqual(existing))
        {
            return existing;
        }

        string created = value.ToString();
        _slots[slot] = created;
        return created;
    }
}
