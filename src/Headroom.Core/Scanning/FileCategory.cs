namespace Headroom.Core.Scanning;

/// <summary>Categoria de um arquivo, derivada da extensão.</summary>
public enum FileCategory : byte
{
    Other = 0,
    Video,
    Image,
    Audio,
    Document,
    Archive,
    Installer,
    Code,
    System,
}

public static class FileCategoryInfo
{
    public static readonly FileCategory[] All =
    [
        FileCategory.Video, FileCategory.Image, FileCategory.Audio, FileCategory.Document, FileCategory.Archive,
        FileCategory.Installer, FileCategory.Code, FileCategory.System, FileCategory.Other,
    ];

    public static int Count => All.Length;

    /// <summary>Nome exibido na interface (pt-BR).</summary>
    public static string DisplayName(FileCategory category) => category switch
    {
        FileCategory.Video => "Vídeos",
        FileCategory.Image => "Imagens",
        FileCategory.Audio => "Áudio",
        FileCategory.Document => "Documentos",
        FileCategory.Archive => "Compactados",
        FileCategory.Installer => "Instaladores",
        FileCategory.Code => "Código/Dev",
        FileCategory.System => "Sistema",
        _ => "Outros",
    };

    /// <summary>Cor (hex RGB) usada no treemap e nos gráficos.</summary>
    public static string ColorHex(FileCategory category) => category switch
    {
        FileCategory.Video => "#F76707",
        FileCategory.Image => "#37B24D",
        FileCategory.Audio => "#AE3EC9",
        FileCategory.Document => "#1C7ED6",
        FileCategory.Archive => "#F59F00",
        FileCategory.Installer => "#E03131",
        FileCategory.Code => "#0CA678",
        FileCategory.System => "#868E96",
        _ => "#5C7CFA",
    };

    public static bool TryParse(string? text, out FileCategory category)
    {
        foreach (var c in All)
        {
            if (string.Equals(DisplayName(c), text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                category = c;
                return true;
            }
        }

        category = FileCategory.Other;
        return false;
    }
}
