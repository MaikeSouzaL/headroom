using System.Drawing;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Headroom.Core.Duplicates;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;

namespace Headroom.Core.Cleanup.Rules;

/// <summary>Base das regras de arquivos do usuário: sempre "Revisar" e nada vem marcado.</summary>
public abstract class UserFileRuleBase : CleanupRuleBase
{
    protected UserFileRuleBase(string id, string name, string description)
        : base(id, CleanupCategory.UserFiles, name, description, RiskLevel.Review)
    {
        EnabledByDefault = false;
        ItemsSelectedByDefault = false;
    }

    /// <summary>Pastas pessoais analisadas (Área de Trabalho, Documentos, Downloads, Imagens, Vídeos, Música).</summary>
    protected static IEnumerable<string> UserFolders(CleanupContext context) =>
        new[] { "DESKTOP", "DOCUMENTS", "DOWNLOADS", "PICTURES", "VIDEOS", "MUSIC" }
            .Select(context.Paths.Resolve)
            .OfType<string>()
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Arquivos locais (nunca links nem arquivos só na nuvem, que seriam baixados ao ler).</summary>
    protected static IEnumerable<FileInfo> LocalFiles(string root, bool recursive = true)
    {
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using var enumerator = files.GetEnumerator();
        while (true)
        {
            FileInfo current;
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                current = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            if (!DiskScanner.IsCloudOnly(current.Attributes))
            {
                yield return current;
            }
        }
    }

    protected CleanupAnalysis Result(List<CleanupItem> items, string? note = null)
    {
        foreach (var item in items)
        {
            item.IsSelected = false;
        }

        return new CleanupAnalysis(this, items.OrderByDescending(i => i.Size).ToList(), null, note);
    }
}

/// <summary>Arquivos grandes e antigos nas pastas pessoais.</summary>
public sealed class LargeOldFilesRule : UserFileRuleBase
{
    public LargeOldFilesRule()
        : base("user.largeold", "Arquivos grandes e antigos",
            "Arquivos pessoais acima do tamanho mínimo e sem modificação há muitos meses (configurável). Revise um a um.")
    {
    }

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            long min = context.Settings.OldLargeFileMinimumBytes;
            var cutoff = DateTime.Now.AddMonths(-context.Settings.OldLargeFileMonths);
            var items = UserFolders(context)
                .SelectMany(folder => LocalFiles(folder))
                .Where(f => f.Length >= min && f.LastWriteTime < cutoff)
                .Select(f => new CleanupItem(f.FullName, f.Length, false, f.LastWriteTime)
                {
                    Reason = $"{ByteSize.Format(f.Length)}, sem modificação desde {f.LastWriteTime:dd/MM/yyyy}",
                })
                .ToList();
            return Result(items, $"Arquivos ≥ {ByteSize.Format(min)} sem modificação há mais de {context.Settings.OldLargeFileMonths} meses.");
        }, cancellationToken);
}

/// <summary>Duplicados nas pastas pessoais (tamanho → hash parcial → hash completo), mantendo o mais antigo.</summary>
public sealed class UserDuplicatesRule : UserFileRuleBase
{
    public UserDuplicatesRule()
        : base("user.duplicates", "Arquivos duplicados",
            "Cópias idênticas nas pastas pessoais. Em cada grupo o mais antigo (provável original) é mantido e as cópias são listadas.")
    {
    }

    public override async Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        var candidates = await Task.Run(() => UserFolders(context)
            .SelectMany(folder => LocalFiles(folder))
            .Select(f => new FileCandidate(f.FullName, f.Length, f.LastWriteTimeUtc))
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList(), cancellationToken).ConfigureAwait(false);

        var groups = await new DuplicateFinder().FindAsync(candidates,
            new DuplicateOptions { MinimumSize = context.Settings.DuplicateMinimumBytes }, null, cancellationToken).ConfigureAwait(false);

        var items = new List<CleanupItem>();
        foreach (var group in groups)
        {
            var keep = group.SuggestedKeep;
            foreach (var copy in group.Files.Where(f => !ReferenceEquals(f, keep)))
            {
                items.Add(new CleanupItem(copy.Path, copy.Size, false, copy.LastWriteUtc.ToLocalTime())
                {
                    Reason = $"Cópia idêntica de {keep.Path}",
                });
            }
        }

        return Result(items, $"{groups.Count} grupo(s) de duplicados. A tela \"Duplicados\" permite escolher qual cópia manter.");
    }
}

/// <summary>Imagens muito parecidas (hash perceptual dHash de 64 bits), mantendo a maior de cada grupo.</summary>
public sealed class SimilarImagesRule : UserFileRuleBase
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"];

    public SimilarImagesRule()
        : base("user.similarimages", "Imagens parecidas (opcional)",
            "Fotos quase iguais (rajadas, versões redimensionadas) nas pastas de imagens, comparadas por hash perceptual. " +
            "A maior de cada grupo é mantida. Pode demorar em coleções grandes.")
    {
    }

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var folders = new[] { "PICTURES", "DESKTOP", "DOWNLOADS" }.Select(context.Paths.Resolve).OfType<string>()
                .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
            var images = folders.SelectMany(f => LocalFiles(f))
                .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase) && f.Length is > 10_000 and < 80_000_000)
                .Take(8000)
                .ToList();

            var hashes = new List<(FileInfo File, ulong Hash)>();
            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ComputeDHash(image.FullName) is { } hash)
                {
                    hashes.Add((image, hash));
                }
            }

            var items = new List<CleanupItem>();
            var used = new bool[hashes.Count];
            for (int i = 0; i < hashes.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                var group = new List<int> { i };
                for (int j = i + 1; j < hashes.Count; j++)
                {
                    if (!used[j] && BitOperations.PopCount(hashes[i].Hash ^ hashes[j].Hash) <= 5)
                    {
                        group.Add(j);
                    }
                }

                if (group.Count < 2)
                {
                    continue;
                }

                foreach (int g in group)
                {
                    used[g] = true;
                }

                var keep = group.OrderByDescending(g => hashes[g].File.Length).First();
                foreach (int g in group.Where(g => g != keep))
                {
                    var f = hashes[g].File;
                    items.Add(new CleanupItem(f.FullName, f.Length, false, f.LastWriteTime)
                    {
                        Reason = $"Parecida com {hashes[keep].File.FullName}",
                    });
                }
            }

            return Result(items, $"{hashes.Count} imagens comparadas.");
        }, cancellationToken);

    /// <summary>dHash: reduz para 9×8 tons de cinza e compara pixels vizinhos (64 bits).</summary>
    internal static ulong? ComputeDHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            using var small = new Bitmap(9, 8);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(image, 0, 0, 9, 8);
            }

            ulong hash = 0;
            int bit = 0;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    var left = small.GetPixel(x, y);
                    var right = small.GetPixel(x + 1, y);
                    double l = 0.299 * left.R + 0.587 * left.G + 0.114 * left.B;
                    double r = 0.299 * right.R + 0.587 * right.G + 0.114 * right.B;
                    if (l > r)
                    {
                        hash |= 1UL << bit;
                    }

                    bit++;
                }
            }

            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or ExternalException)
        {
            return null;
        }
    }
}

/// <summary>Instaladores e compactados esquecidos em Downloads/Documentos/Área de Trabalho.</summary>
public sealed class ForgottenInstallersRule : UserFileRuleBase
{
    private static readonly string[] Extensions = [".exe", ".msi", ".msix", ".iso", ".zip", ".7z", ".rar"];

    public ForgottenInstallersRule()
        : base("user.installers", "Instaladores esquecidos",
            "Arquivos .exe, .msi, .iso e .zip em Downloads, Documentos e Área de Trabalho com mais de 30 dias.")
    {
    }

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var cutoff = DateTime.Now.AddDays(-30);
            var items = new[] { "DOWNLOADS", "DOCUMENTS", "DESKTOP" }.Select(context.Paths.Resolve).OfType<string>()
                .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(f => LocalFiles(f))
                .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase) && f.LastWriteTime < cutoff)
                .Select(f => new CleanupItem(f.FullName, f.Length, false, f.LastWriteTime)
                {
                    Reason = $"{FileCategoryInfo.DisplayName(CategoryClassifier.Classify(f.Name))} de {f.LastWriteTime:dd/MM/yyyy}",
                })
                .ToList();
            return Result(items);
        }, cancellationToken);
}

/// <summary>ZIP/RAR/7z cujo conteúdo já foi extraído para uma pasta ao lado.</summary>
public sealed class ExtractedArchivesRule : UserFileRuleBase
{
    private static readonly string[] Extensions = [".zip", ".rar", ".7z"];

    public ExtractedArchivesRule()
        : base("user.extracted", "Compactados já extraídos",
            "Arquivos .zip/.rar/.7z que têm ao lado uma pasta com o mesmo nome contendo o conteúdo. Para .zip, o conteúdo é conferido.")
    {
    }

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var items = new List<CleanupItem>();
            foreach (var archive in new[] { "DOWNLOADS", "DOCUMENTS", "DESKTOP" }.Select(context.Paths.Resolve).OfType<string>()
                         .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
                         .SelectMany(f => LocalFiles(f))
                         .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string folder = Path.Combine(archive.DirectoryName!, Path.GetFileNameWithoutExtension(archive.Name));
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                string? reason = null;
                if (archive.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    double coverage = ZipCoverage(archive.FullName, folder);
                    if (coverage >= 0.9)
                    {
                        reason = $"{coverage:P0} do conteúdo já existe em {folder}";
                    }
                }
                else
                {
                    reason = $"Existe a pasta {folder} ao lado (conteúdo não conferido para {archive.Extension})";
                }

                if (reason is not null)
                {
                    items.Add(new CleanupItem(archive.FullName, archive.Length, false, archive.LastWriteTime) { Reason = reason });
                }
            }

            return Result(items);
        }, cancellationToken);

    /// <summary>Fração das entradas do ZIP que existem na pasta (com ou sem a pasta-raiz do ZIP).</summary>
    internal static double ZipCoverage(string zipPath, string folder)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            if (files.Count == 0)
            {
                return 0;
            }

            int found = 0;
            foreach (var entry in files)
            {
                string relative = entry.FullName.Replace('/', '\\');
                string withoutRoot = relative.Contains('\\') ? relative[(relative.IndexOf('\\') + 1)..] : relative;
                if (File.Exists(Path.Combine(folder, relative)) || File.Exists(Path.Combine(folder, withoutRoot)))
                {
                    found++;
                }
            }

            return found / (double)files.Count;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return 0;
        }
    }
}

/// <summary>Pastas vazias (sem nenhum arquivo dentro, recursivamente) nas pastas pessoais.</summary>
public sealed class EmptyFoldersRule : UserFileRuleBase
{
    public EmptyFoldersRule()
        : base("user.emptyfolders", "Pastas vazias",
            "Pastas sem nenhum arquivo (nem em subpastas) dentro de Documentos, Downloads, Área de Trabalho, Imagens, Vídeos e Música.")
    {
    }

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var items = new List<CleanupItem>();
            foreach (var root in UserFolders(context))
            {
                foreach (var sub in SafeDirs(root))
                {
                    var (empty, found) = CollectEmpty(sub, cancellationToken);
                    if (empty)
                    {
                        items.Add(new CleanupItem(sub.FullName, 0, true, sub.LastWriteTime, 0) { Reason = "Pasta vazia" });
                    }
                    else
                    {
                        items.AddRange(found);
                    }
                }
            }

            return Result(items);
        }, cancellationToken);

    /// <summary>
    /// Diz se a pasta está vazia e, se não estiver, devolve as subpastas vazias mais externas dela.
    /// </summary>
    private static (bool Empty, List<CleanupItem> Found) CollectEmpty(DirectoryInfo dir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var found = new List<CleanupItem>();
        if ((dir.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0)
        {
            return (false, found);
        }

        bool hasFiles;
        try
        {
            hasFiles = dir.EnumerateFiles().Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, found);
        }

        var emptyChildren = new List<DirectoryInfo>();
        bool allChildrenEmpty = true;
        foreach (var child in SafeDirs(dir.FullName))
        {
            var (childEmpty, childFound) = CollectEmpty(child, ct);
            if (childEmpty)
            {
                emptyChildren.Add(child);
            }
            else
            {
                allChildrenEmpty = false;
                found.AddRange(childFound);
            }
        }

        if (!hasFiles && allChildrenEmpty)
        {
            return (true, new List<CleanupItem>());
        }

        foreach (var child in emptyChildren)
        {
            found.Add(new CleanupItem(child.FullName, 0, true, child.LastWriteTime, 0) { Reason = "Pasta vazia" });
        }

        return (false, found);
    }

    private static IEnumerable<DirectoryInfo> SafeDirs(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<DirectoryInfo>();
        }
    }
}

/// <summary>Atalhos (.lnk) cujo destino local não existe mais (Área de Trabalho e Menu Iniciar do usuário).</summary>
public sealed class BrokenShortcutsRule : UserFileRuleBase
{
    public BrokenShortcutsRule()
        : base("user.brokenshortcuts", "Atalhos quebrados",
            "Atalhos da Área de Trabalho e do Menu Iniciar que apontam para arquivos/pastas que não existem mais.")
    {
    }

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        StaThreadRunner(() =>
        {
            var folders = new[] { context.Paths.Resolve("DESKTOP"), context.Paths.Resolve("STARTMENU") }
                .OfType<string>().Where(Directory.Exists);
            var items = new List<CleanupItem>();
            foreach (var lnk in folders.SelectMany(f => LocalFiles(f)).Where(f => f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? target = ResolveShortcut(lnk.FullName);
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue; // atalhos para itens do shell, URLs etc.
                }

                string? root = Path.GetPathRoot(target);
                bool driveReady = root is not null && !root.StartsWith(@"\\", StringComparison.Ordinal) &&
                                  DriveInfo.GetDrives().Any(d => d.IsReady && d.DriveType == DriveType.Fixed &&
                                                                 d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
                if (driveReady && !File.Exists(target) && !Directory.Exists(target))
                {
                    items.Add(new CleanupItem(lnk.FullName, lnk.Length, false, lnk.LastWriteTime) { Reason = $"Destino não existe: {target}" });
                }
            }

            return Result(items);
        });

    private static Task<T> StaThreadRunner<T>(Func<T> work) => Deletion.StaThread.RunAsync(work);

    internal static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                var buffer = new StringBuilder(1024);
                link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
                return Environment.ExpandEnvironmentVariables(buffer.ToString());
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException or InvalidCastException)
        {
            return null;
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }
}
