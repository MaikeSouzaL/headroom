namespace Headroom.Core.Scanning;

/// <summary>Identifica arquivos que já estão na Lixeira ($Recycle.Bin na raiz de cada disco).</summary>
public static class RecycleBinPath
{
    private const string Segment = @"\$Recycle.Bin\";

    /// <summary>
    /// Verdadeiro para caminhos dentro de X:\$Recycle.Bin. Esses arquivos já foram "apagados": não são
    /// duplicados a resolver nem faz sentido movê-los para a Lixeira; o espaço volta ao esvaziar a Lixeira.
    /// </summary>
    public static bool Contains(string fullPath)
    {
        int index = fullPath.IndexOf(Segment, StringComparison.OrdinalIgnoreCase);

        // Só conta quando está logo abaixo da raiz do disco (ex.: "E:\$Recycle.Bin\...").
        return index >= 0 && fullPath.LastIndexOf('\\', Math.Max(0, index - 1)) < 0;
    }
}
