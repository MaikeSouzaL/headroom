using System.Collections.Concurrent;

namespace Headroom.Core.Cleanup.Rules;

/// <summary>
/// Arquivos já somados numa análise, para que um arquivo com vários hardlinks entre na soma uma vez só
/// (como o <c>du</c>). O uv, o pnpm e o conda ligam o mesmo arquivo em centenas de lugares; somar cada
/// caminho chegava a mostrar mais do que o disco inteiro.
/// </summary>
/// <remarks>
/// A chave é o número do registro na MFT (os 48 bits baixos do file ID do NTFS), que vem na própria listagem
/// da pasta: não é preciso abrir nenhum arquivo. Os bits ficam em páginas de 8 KB criadas sob demanda, então
/// mesmo um volume com dezenas de milhões de arquivos custa poucos MB. Só vale dentro de um volume.
/// </remarks>
internal sealed class HardLinkSet
{
    private const int PageBits = 16;
    private const long PageMask = (1L << PageBits) - 1;
    private const long RecordMask = (1L << 48) - 1;

    private readonly ConcurrentDictionary<(uint Volume, long Page), long[]> _pages = new();

    /// <summary>Marca o arquivo; devolve true na primeira vez que ele aparece (só então o tamanho conta).</summary>
    public bool TryAdd(uint volumeSerial, long fileId)
    {
        long record = fileId & RecordMask;
        var page = _pages.GetOrAdd((volumeSerial, record >> PageBits), static _ => new long[(1 << PageBits) / 64]);
        int bit = (int)(record & PageMask);
        long mask = 1L << (bit & 63);
        return (Interlocked.Or(ref page[bit >> 6], mask) & mask) == 0;
    }
}
