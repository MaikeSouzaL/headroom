namespace Headroom.Core.Scanning;

/// <summary>
/// Classifica arquivos em categorias a partir da extensão. Thread-safe (somente leitura).
/// </summary>
public static class CategoryClassifier
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);

        void Add(FileCategory category, params string[] extensions)
        {
            foreach (var ext in extensions)
            {
                map[ext] = category;
            }
        }

        Add(FileCategory.Video, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg",
            ".3gp", ".ts", ".m2ts", ".mts", ".vob", ".ogv", ".rmvb", ".divx", ".f4v", ".braw", ".r3d", ".prproj");
        Add(FileCategory.Image, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif",
            ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".raf", ".psd", ".psb", ".ai", ".svg",
            ".ico", ".jfif", ".avif", ".xcf", ".exr", ".hdr", ".tga", ".dds", ".kra", ".blend");
        Add(FileCategory.Audio, ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".aiff", ".aif",
            ".mid", ".midi", ".ape", ".alac", ".m4b", ".amr");
        Add(FileCategory.Document, ".pdf", ".doc", ".docx", ".docm", ".xls", ".xlsx", ".xlsm", ".xlsb", ".ppt",
            ".pptx", ".odt", ".ods", ".odp", ".rtf", ".txt", ".md", ".csv", ".epub", ".mobi", ".pages", ".numbers",
            ".key", ".one", ".xps", ".oxps", ".djvu", ".tex", ".msg", ".eml", ".pst", ".ost", ".vsdx", ".pub");
        Add(FileCategory.Archive, ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab",
            ".lz", ".lzma", ".z", ".tbz2", ".txz", ".arj", ".lzh");
        Add(FileCategory.Installer, ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".iso",
            ".img", ".dmg", ".msp", ".msu", ".apk", ".aab", ".deb", ".rpm", ".wim", ".esd", ".ipsw", ".pkg");
        Add(FileCategory.Code, ".cs", ".vb", ".fs", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".py", ".pyc",
            ".pyd", ".java", ".class", ".jar", ".kt", ".kts", ".go", ".rs", ".c", ".cc", ".cpp", ".cxx", ".h", ".hpp",
            ".json", ".xml", ".yml", ".yaml", ".toml", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".sql",
            ".sh", ".ps1", ".psm1", ".bat", ".cmd", ".rb", ".php", ".swift", ".dart", ".lua", ".r", ".ipynb",
            ".sln", ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".props", ".targets", ".gradle", ".lock", ".map",
            ".wasm", ".pdb", ".obj", ".o", ".lib", ".a", ".whl", ".nupkg", ".snupkg", ".vue", ".svelte", ".astro",
            ".ilk", ".ipch", ".pch", ".tlog", ".cache", ".resx", ".xaml", ".razor", ".cshtml", ".gem", ".node");
        Add(FileCategory.System, ".dll", ".sys", ".drv", ".ocx", ".cpl", ".mui", ".cat", ".inf", ".etl", ".evtx",
            ".log", ".dmp", ".mdmp", ".hdmp", ".tmp", ".temp", ".bak", ".old", ".pf", ".db", ".db-wal", ".db-shm",
            ".dat", ".bin", ".vhd", ".vhdx", ".vmdk", ".vdi", ".qcow2", ".avhdx", ".edb", ".jrs", ".regtrans-ms",
            ".blf", ".sdb", ".manifest", ".mum", ".ini", ".lnk", ".nls", ".ttf", ".otf", ".fon", ".ttc", ".msc",
            ".scr", ".efi", ".chk", ".jfm", ".ldb", ".sqlite", ".sqlite-wal", ".sqlite-shm", ".cab_", ".p7x", ".psf");

        return map;
    }

    /// <summary>Retorna a extensão (com ponto) ou string vazia. Nomes iniciados por ponto não têm extensão.</summary>
    public static string GetExtension(string fileName)
    {
        var span = GetExtensionSpan(fileName);
        return span.IsEmpty ? string.Empty : span.ToString();
    }

    /// <summary>Versão sem alocação de <see cref="GetExtension"/>.</summary>
    public static ReadOnlySpan<char> GetExtensionSpan(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1 || fileName.Length - dot > 16)
        {
            return ReadOnlySpan<char>.Empty;
        }

        return fileName[dot..];
    }

    public static FileCategory ClassifyExtension(string extension)
    {
        if (extension.Length == 0)
        {
            return FileCategory.Other;
        }

        return Map.TryGetValue(extension, out var category) ? category : FileCategory.Other;
    }

    public static FileCategory Classify(string fileName) => ClassifyExtension(GetExtension(fileName));
}
