using System.Text.Json;
using System.Text.RegularExpressions;
using Headroom.Core.Cleanup.Safety;
using Headroom.Core.Formatting;
using Headroom.Core.SystemTools;

namespace Headroom.Core.Cleanup.Rules;

/// <summary>
/// Projetos parados: pastas geradas (node_modules, bin/obj, .venv, target, dist, build, .next...) de
/// projetos sem modificação há X dias. A data do projeto ignora as próprias pastas geradas e o .git.
/// </summary>
public sealed class StaleProjectsRule : CleanupRuleBase
{
    private static readonly string[] SkipFolders =
    [
        "AppData", ".git", ".hg", ".svn", "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", ".cache", ".nuget", ".gradle\\caches", ".m2", ".cargo", "go", ".rustup", "scoop",
    ];

    // Pastas geradas por tipo de projeto (sempre marcadas) e as "ambíguas" (desmarcadas por padrão).
    private static readonly Dictionary<string, (string[] Markers, bool Ambiguous)> ArtifactRules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_modules"] = (["package.json"], false),
        [".next"] = (["package.json"], false),
        [".nuxt"] = (["package.json"], false),
        [".output"] = (["package.json"], true),
        [".turbo"] = (["package.json"], false),
        [".parcel-cache"] = (["package.json"], false),
        [".angular"] = (["angular.json", "package.json"], false),
        [".svelte-kit"] = (["package.json"], false),
        ["dist"] = (["package.json"], true),
        ["build"] = (["package.json", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"], true),
        ["bin"] = (["*.csproj", "*.fsproj", "*.vbproj", "*.sln"], false),
        ["obj"] = (["*.csproj", "*.fsproj", "*.vbproj", "*.sln"], false),
        [".vs"] = (["*.sln", "*.csproj"], false),
        [".venv"] = (["pyproject.toml", "requirements.txt", "setup.py", "Pipfile", "*.py"], false),
        ["venv"] = (["pyproject.toml", "requirements.txt", "setup.py", "Pipfile", "*.py"], false),
        ["__pycache__"] = (["pyproject.toml", "requirements.txt", "setup.py", "Pipfile", "*.py"], false),
        [".pytest_cache"] = (["pyproject.toml", "requirements.txt", "setup.py", "*.py"], false),
        [".mypy_cache"] = (["pyproject.toml", "requirements.txt", "setup.py", "*.py"], false),
        ["target"] = (["Cargo.toml", "pom.xml"], false),
        [".gradle"] = (["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"], false),
    };

    public StaleProjectsRule()
        : base("dev.staleprojects", CleanupCategory.Developer, "Projetos parados: pastas geradas",
            "node_modules, bin/obj, .venv, __pycache__, target, dist, build, .next, .nuxt, .turbo, .parcel-cache, .angular, " +
            ".gradle e .vs de projetos sem modificação há X dias (configurável). Tudo pode ser recriado pelo gerenciador de " +
            "pacotes/compilador do projeto.", RiskLevel.Review)
    {
        EnabledByDefault = false;
    }

    public override async Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        int days = context.Settings.StaleProjectDays;
        var cutoff = DateTime.Now.AddDays(-days);
        var roots = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        roots.AddRange(context.Settings.DevProjectRoots.Where(Directory.Exists));

        var items = new List<CleanupItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await Task.Run(() =>
        {
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Walk(root, 0, cutoff, items, seen, cancellationToken);
            }

            // Artefatos encontrados no último scan (ex.: projetos em outros discos).
            if (context.LastScan is { } scan)
            {
                foreach (var artifact in scan.DevArtifacts)
                {
                    string? project = Path.GetDirectoryName(artifact.Path);
                    if (project is not null && seen.Add(artifact.Path) && Directory.Exists(artifact.Path))
                    {
                        var lastChange = ProjectLastModified(project, cancellationToken);
                        if (lastChange < cutoff)
                        {
                            items.Add(MakeItem(artifact.Path, Path.GetFileName(artifact.Path), project, lastChange, false, cancellationToken));
                        }
                    }
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        return new CleanupAnalysis(this, items.OrderByDescending(i => i.Size).ToList(), null,
            $"Projetos sem modificação há mais de {days} dias. Pastas ambíguas (dist, build) vêm desmarcadas.");
    }

    private void Walk(string directory, int depth, DateTime cutoff, List<CleanupItem> items, HashSet<string> seen, CancellationToken ct)
    {
        if (depth > 8)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var fileNames = entries.OfType<FileInfo>().Select(f => f.Name).ToList();
        var subdirs = entries.OfType<DirectoryInfo>().ToList();
        DateTime? projectDate = null;

        foreach (var dir in subdirs)
        {
            if (ArtifactRules.TryGetValue(dir.Name, out var rule) && rule.Markers.Any(m => fileNames.Any(f => PathExpander.MatchesWildcard(f, m))))
            {
                if (dir.Name.Equals("venv", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(dir.FullName, "pyvenv.cfg")))
                {
                    continue;
                }

                projectDate ??= ProjectLastModified(directory, ct);
                if (projectDate < cutoff && seen.Add(dir.FullName))
                {
                    items.Add(MakeItem(dir.FullName, dir.Name, directory, projectDate.Value, rule.Ambiguous, ct));
                }

                continue; // nunca entra em pastas geradas
            }

            if (SkipFolders.Any(s => dir.Name.Equals(s, StringComparison.OrdinalIgnoreCase)) || dir.Name.StartsWith('.') && depth == 0)
            {
                continue;
            }

            Walk(dir.FullName, depth + 1, cutoff, items, seen, ct);
        }
    }

    private static CleanupItem MakeItem(string path, string name, string project, DateTime lastChange, bool ambiguous, CancellationToken ct)
    {
        var (size, files, newest) = PathRule.MeasureDirectory(path, ct);
        return new CleanupItem(path, size, true, newest == DateTime.MinValue ? lastChange : newest, files)
        {
            DisplayName = $"{name} — {Path.GetFileName(project)}",
            Reason = $"Projeto sem modificação desde {lastChange:dd/MM/yyyy}" + (ambiguous ? " (pasta ambígua: confira antes)" : string.Empty),
            IsSelected = !ambiguous,
        };
    }

    /// <summary>Data mais recente dos arquivos do projeto, ignorando pastas geradas e controle de versão.</summary>
    public static DateTime ProjectLastModified(string project, CancellationToken ct)
    {
        DateTime newest = DateTime.MinValue;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((project, 0));
        int visited = 0;
        while (stack.Count > 0 && visited < 20_000)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions
                         {
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
                {
                    visited++;
                    if (entry is DirectoryInfo sub)
                    {
                        if (depth < 6 && !ArtifactRules.ContainsKey(sub.Name) && !sub.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
                            !sub.Name.Equals(".idea", StringComparison.OrdinalIgnoreCase))
                        {
                            stack.Push((sub.FullName, depth + 1));
                        }
                    }
                    else if (entry.LastWriteTime > newest)
                    {
                        newest = entry.LastWriteTime;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return newest == DateTime.MinValue ? Directory.GetLastWriteTime(project) : newest;
    }
}

/// <summary>
/// Versões antigas mantidas por ferramentas (extensões do VS Code, distribuições do Gradle Wrapper,
/// build-tools/plataformas do Android, configurações de versões antigas de IDEs JetBrains).
/// Mantém as N versões mais novas de cada item.
/// </summary>
public sealed class OldVersionsRule : CleanupRuleBase
{
    private readonly string _folderPattern;
    private readonly Func<string, (string Key, Version Version)?> _parser;
    private readonly int _keep;

    public OldVersionsRule(string id, CleanupCategory category, string name, string description, RiskLevel risk,
        string folderPattern, Func<string, (string Key, Version Version)?> parser, int keep = 1)
        : base(id, category, name, description, risk)
    {
        _folderPattern = folderPattern;
        _parser = parser;
        _keep = keep;
    }

    /// <summary>Pastas que nunca são consideradas antigas (ex.: referenciadas pelo extensions.json do VS Code).</summary>
    public Func<string, HashSet<string>>? ProtectedNames { get; init; }

    public override bool IsApplicable(CleanupContext context) => context.Paths.Expand(_folderPattern).Count > 0;

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var items = new List<CleanupItem>();
            foreach (var parent in context.Paths.Expand(_folderPattern).Where(Directory.Exists))
            {
                var keepSet = ProtectedNames?.Invoke(parent) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var versions = new List<(DirectoryInfo Dir, string Key, Version Version)>();
                foreach (var dir in new DirectoryInfo(parent).EnumerateDirectories("*", new EnumerationOptions
                         {
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
                {
                    if (_parser(dir.Name) is { } parsed)
                    {
                        versions.Add((dir, parsed.Key, parsed.Version));
                    }
                }

                foreach (var group in versions.GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var ordered = group.OrderByDescending(v => v.Version).ToList();
                    foreach (var old in ordered.Skip(_keep))
                    {
                        if (keepSet.Contains(old.Dir.Name))
                        {
                            continue;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        var (size, files, newest) = PathRule.MeasureDirectory(old.Dir.FullName, cancellationToken);
                        items.Add(new CleanupItem(old.Dir.FullName, size, true, newest == DateTime.MinValue ? old.Dir.LastWriteTime : newest, files)
                        {
                            Reason = $"Versão {old.Version}; mantida a mais nova: {ordered[0].Version}",
                            IsSelected = ItemsSelectedByDefault,
                        });
                    }
                }
            }

            return new CleanupAnalysis(this, items.OrderByDescending(i => i.Size).ToList());
        }, cancellationToken);

    /// <summary>"publisher.name-1.2.3" ou "publisher.name-1.2.3-win32-x64" (extensões do VS Code).</summary>
    public static (string, Version)? ParseVsCodeExtension(string folder)
    {
        var m = Regex.Match(folder, @"^(?<id>[^\s]+?)-(?<v>\d+\.\d+\.\d+)(?:-.+)?$");
        return m.Success && Version.TryParse(m.Groups["v"].Value, out var v) ? (m.Groups["id"].Value + Platform(folder), v) : null;

        static string Platform(string f)
        {
            var p = Regex.Match(f, @"\d+\.\d+\.\d+-(?<p>.+)$");
            return p.Success ? "|" + p.Groups["p"].Value : string.Empty;
        }
    }

    /// <summary>"gradle-8.5-bin", "gradle-7.6.1-all".</summary>
    public static (string, Version)? ParseGradleDist(string folder)
    {
        var m = Regex.Match(folder, @"^gradle-(?<v>\d+(?:\.\d+){1,3})(?:-[a-z0-9]+)*-(?<t>bin|all)$", RegexOptions.IgnoreCase);
        return m.Success && Version.TryParse(Normalize(m.Groups["v"].Value), out var v) ? ("gradle-" + m.Groups["t"].Value, v) : null;
    }

    /// <summary>"34.0.0" (build-tools) ou "android-34" (platforms).</summary>
    public static (string, Version)? ParseAndroidVersion(string folder)
    {
        string digits = folder.StartsWith("android-", StringComparison.OrdinalIgnoreCase) ? folder[8..] : folder;
        digits = Regex.Replace(digits, "[^0-9.]", string.Empty);
        return Version.TryParse(Normalize(digits), out var v) ? ("android", v) : null;
    }

    /// <summary>"IntelliJIdea2023.2", "PyCharm2024.1", "Rider2023.3" (pastas de configuração/cache das IDEs JetBrains).</summary>
    public static (string, Version)? ParseJetBrains(string folder)
    {
        var m = Regex.Match(folder, @"^(?<p>[A-Za-z]+)(?<v>20\d{2}\.\d+)$");
        return m.Success && Version.TryParse(m.Groups["v"].Value, out var v) ? (m.Groups["p"].Value, v) : null;
    }

    private static string Normalize(string v) => v.Contains('.') ? v : v + ".0";

    /// <summary>Extensões referenciadas pelo extensions.json do VS Code (nunca removidas).</summary>
    public static HashSet<string> VsCodeReferencedExtensions(string extensionsFolder)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string json = Path.Combine(extensionsFolder, "extensions.json");
            if (File.Exists(json))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                foreach (var ext in doc.RootElement.EnumerateArray())
                {
                    if (ext.TryGetProperty("relativeLocation", out var rel) && rel.GetString() is { } r)
                    {
                        set.Add(r);
                    }
                    else if (ext.TryGetProperty("location", out var loc) && loc.TryGetProperty("path", out var p) && p.GetString() is { } path)
                    {
                        set.Add(Path.GetFileName(path.TrimEnd('/')));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
        }

        return set;
    }
}

/// <summary>workspaceStorage do VS Code (e derivados) de pastas/projetos que não existem mais.</summary>
public sealed class VsCodeWorkspaceStorageRule : CleanupRuleBase
{
    private static readonly string[] Products = ["Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf"];

    public VsCodeWorkspaceStorageRule()
        : base("dev.vscode.workspacestorage", CleanupCategory.Developer, "VS Code: dados de workspaces órfãos",
            "Dados de workspace (workspaceStorage) de pastas que não existem mais no disco (VS Code, Insiders, VSCodium, Cursor, Windsurf).",
            RiskLevel.Review)
    {
        ProcessesToClose = ["Code.exe", "Code - Insiders.exe", "VSCodium.exe", "Cursor.exe", "Windsurf.exe"];
    }

    private static IEnumerable<string> StorageFolders() =>
        Products.Select(p => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), p, "User", "workspaceStorage"))
            .Where(Directory.Exists);

    public override bool IsApplicable(CleanupContext context) => StorageFolders().Any();

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var items = new List<CleanupItem>();
            foreach (var storage in StorageFolders())
            {
                foreach (var dir in new DirectoryInfo(storage).EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? target = ReadWorkspaceTarget(Path.Combine(dir.FullName, "workspace.json"));
                    if (target is null)
                    {
                        continue; // remoto ou desconhecido: nunca considera órfão
                    }

                    bool exists = Directory.Exists(target) || File.Exists(target);
                    string? root = Path.GetPathRoot(target);
                    bool driveReady = root is not null && DriveInfo.GetDrives().Any(d => d.IsReady && d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
                    if (exists || !driveReady)
                    {
                        continue; // disco desconectado não conta como "apagado"
                    }

                    var (size, files, newest) = PathRule.MeasureDirectory(dir.FullName, cancellationToken);
                    items.Add(new CleanupItem(dir.FullName, size, true, newest, files)
                    {
                        DisplayName = Path.GetFileName(target.TrimEnd('\\')) + " (workspace)",
                        Reason = $"A pasta do projeto não existe mais: {target}",
                    });
                }
            }

            return new CleanupAnalysis(this, items.OrderByDescending(i => i.Size).ToList());
        }, cancellationToken);

    internal static string? ReadWorkspaceTarget(string workspaceJson)
    {
        try
        {
            if (!File.Exists(workspaceJson))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(workspaceJson));
            string? uri = (doc.RootElement.TryGetProperty("folder", out var f) ? f.GetString() : null) ??
                          (doc.RootElement.TryGetProperty("workspace", out var w) ? w.GetString() : null);
            if (uri is null || !uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // O VS Code grava "file:///c%3A/..."; com o ":" codificado o Uri não reconhece a letra do disco.
            return new Uri(Uri.UnescapeDataString(uri)).LocalPath;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UriFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Imagens de sistema do Android SDK que nenhum AVD usa.</summary>
public sealed class AndroidSystemImagesRule : CleanupRuleBase
{
    public AndroidSystemImagesRule()
        : base("dev.android.systemimages", CleanupCategory.Developer, "Android: imagens de sistema não usadas",
            "Imagens de emulador (system-images) que nenhum dispositivo virtual (AVD) usa. Podem ser baixadas de novo pelo SDK Manager.",
            RiskLevel.Review)
    {
        ItemsSelectedByDefault = false;
        EnabledByDefault = false;
    }

    public override bool IsApplicable(CleanupContext context) =>
        context.Paths.Expand(@"%ANDROIDSDK%\system-images").Count > 0;

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            string? sdk = context.Paths.Resolve("ANDROIDSDK");
            var items = new List<CleanupItem>();
            if (sdk is null)
            {
                return new CleanupAnalysis(this, items);
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string avdRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd");
            if (Directory.Exists(avdRoot))
            {
                foreach (var config in Directory.EnumerateFiles(avdRoot, "config.ini", SearchOption.AllDirectories))
                {
                    foreach (var line in File.ReadLines(config))
                    {
                        if (line.StartsWith("image.sysdir.1", StringComparison.OrdinalIgnoreCase))
                        {
                            used.Add(Path.GetFullPath(Path.Combine(sdk, line.Split('=', 2)[1].Trim().Replace('/', '\\'))).TrimEnd('\\'));
                        }
                    }
                }
            }

            // system-images\android-34\google_apis\x86_64
            foreach (var abi in context.Paths.Expand(@"%ANDROIDSDK%\system-images\*\*\*"))
            {
                if (!Directory.Exists(abi) || used.Contains(abi.TrimEnd('\\')))
                {
                    continue;
                }

                var (size, files, newest) = PathRule.MeasureDirectory(abi, cancellationToken);
                items.Add(new CleanupItem(abi, size, true, newest, files)
                {
                    DisplayName = string.Join(" / ", abi[(sdk.Length + 1)..].Split('\\').Skip(1)),
                    Reason = "Nenhum AVD usa esta imagem.",
                    IsSelected = false,
                });
            }

            return new CleanupAnalysis(this, items.OrderByDescending(i => i.Size).ToList());
        }, cancellationToken);
}

/// <summary>Modelos do Ollama (lista com tamanho e data; remove só os escolhidos, via "ollama rm").</summary>
public sealed class OllamaModelsRule : CleanupRuleBase
{
    private static readonly string ModelsRoot = Environment.GetEnvironmentVariable("OLLAMA_MODELS") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");

    public OllamaModelsRule()
        : base("dev.ai.ollama", CleanupCategory.Developer, "IA: modelos do Ollama",
            "Lista os modelos baixados pelo Ollama com tamanho e data de uso. Nada vem marcado: escolha quais remover " +
            "(a remoção usa \"ollama rm\").", RiskLevel.Review)
    {
        ItemsSelectedByDefault = false;
        EnabledByDefault = false;
        AlwaysPermanent = true;
    }

    public override bool IsApplicable(CleanupContext context) => Directory.Exists(Path.Combine(ModelsRoot, "manifests"));

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var items = new List<CleanupItem>();
            string manifests = Path.Combine(ModelsRoot, "manifests");
            foreach (var manifest in Directory.EnumerateFiles(manifests, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    long size = 0;
                    if (doc.RootElement.TryGetProperty("layers", out var layers))
                    {
                        size += layers.EnumerateArray().Sum(l => l.TryGetProperty("size", out var s) ? s.GetInt64() : 0);
                    }

                    // manifests\registry.ollama.ai\library\llama3\8b → "llama3:8b"; outros namespaces ficam com prefixo.
                    var parts = Path.GetRelativePath(manifests, manifest).Split('\\');
                    if (parts.Length < 4)
                    {
                        continue;
                    }

                    string ns = parts[^3];
                    string model = parts[^2];
                    string tag = parts[^1];
                    string name = (ns.Equals("library", StringComparison.OrdinalIgnoreCase) ? model : $"{ns}/{model}") + ":" + tag;
                    items.Add(new CleanupItem(manifest, size, false, File.GetLastAccessTime(manifest))
                    {
                        DisplayName = name,
                        Tag = name,
                        Reason = $"Baixado em {File.GetCreationTime(manifest):dd/MM/yyyy}",
                        IsSelected = false,
                    });
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
                {
                }
            }

            return new CleanupAnalysis(this, items.OrderByDescending(i => i.Size).ToList(), null,
                "O tamanho pode contar camadas compartilhadas entre modelos.");
        }, cancellationToken);

    public override async Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new CleanupRunResult(this);
        string? ollama = CommandRunner.FindOnPath("ollama.exe") ??
                         new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe") }
                             .FirstOrDefault(File.Exists);
        if (ollama is null)
        {
            result.Success = false;
            result.Message = "O executável do Ollama não foi encontrado; os modelos não foram removidos.";
            return result;
        }

        execution.Log.Section($"{Name} [{Id}]");
        long before = DiskMeasure.FreeSpace(ModelsRoot);
        foreach (var item in analysis.Items.Where(i => i.IsSelected && i.Tag is not null))
        {
            progress?.Report(new CleanupProgress(Name, item.Name, 0.5));
            var run = await CommandRunner.RunAsync(ollama, $"rm {item.Tag}", TimeSpan.FromMinutes(5), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            execution.Log.Item(run.Success ? "MODELO REMOVIDO" : "ERRO", item.Size, item.Name, run.Success ? null : run.Combined.Trim());
        }

        result.BytesFreed = Math.Max(0, DiskMeasure.FreeSpace(ModelsRoot) - before);
        return result;
    }
}

/// <summary>Compacta os discos virtuais .vhdx do WSL e do Docker Desktop (diskpart compact vdisk).</summary>
public sealed class VhdxCompactRule : CleanupRuleBase
{
    private static readonly string[] Patterns =
    [
        @"%LOCALAPPDATA%\Docker\wsl\data\ext4.vhdx",
        @"%LOCALAPPDATA%\Docker\wsl\disk\docker_data.vhdx",
        @"%LOCALAPPDATA%\Docker\wsl\main\ext4.vhdx",
        @"%LOCALAPPDATA%\Packages\*\LocalState\ext4.vhdx",
        @"%LOCALAPPDATA%\wsl\*\ext4.vhdx",
    ];

    public VhdxCompactRule()
        : base("dev.wsl.compact", CleanupCategory.Developer, "WSL/Docker: compactar discos virtuais (.vhdx)",
            "Os discos virtuais do WSL e do Docker crescem e não encolhem sozinhos. Desliga o WSL (wsl --shutdown) e " +
            "compacta cada .vhdx com o diskpart.", RiskLevel.Advanced)
    {
        RequiresAdmin = true;
        EnabledByDefault = false;
        AlwaysPermanent = true;
    }

    public override bool IsApplicable(CleanupContext context) => Patterns.Any(p => context.Paths.Expand(p).Count > 0);

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        if (!context.IsAdministrator)
        {
            return Task.FromResult(CleanupAnalysis.Unavailable(this, "Requer administrador."));
        }

        var items = Patterns.SelectMany(p => context.Paths.Expand(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new CleanupItem(p, DiskMeasure.SafeFileLength(p), false, File.GetLastWriteTime(p))
            {
                Reason = "Tamanho atual do arquivo; o ganho depende do espaço livre dentro do disco virtual.",
            })
            .ToList();
        return Task.FromResult(new CleanupAnalysis(this, items, 0,
            "Estimativa indisponível antes de compactar (depende do espaço livre dentro do disco virtual)."));
    }

    public override async Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new CleanupRunResult(this);
        execution.Log.Section($"{Name} [{Id}]");
        progress?.Report(new CleanupProgress(Name, "Desligando o WSL (wsl --shutdown)", 0.1));
        await CommandRunner.RunAsync(CommandRunner.SystemTool("wsl.exe"), "--shutdown", TimeSpan.FromMinutes(2),
            encoding: System.Text.Encoding.Unicode, cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var item in analysis.Items.Where(i => i.IsSelected))
        {
            long before = DiskMeasure.SafeFileLength(item.Path);
            string script = Path.Combine(Path.GetTempPath(), $"headroom-diskpart-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(script,
                $"select vdisk file=\"{item.Path}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n", cancellationToken)
                .ConfigureAwait(false);
            try
            {
                progress?.Report(new CleanupProgress(Name, "Compactando " + item.Path, 0.5));
                var run = await CommandRunner.RunAsync(CommandRunner.SystemTool("diskpart.exe"), $"/s \"{script}\"",
                    TimeSpan.FromHours(1), cancellationToken: cancellationToken).ConfigureAwait(false);
                long after = DiskMeasure.SafeFileLength(item.Path);
                result.BytesFreed += Math.Max(0, before - after);
                execution.Log.Item(run.Success ? "COMPACTADO" : "ERRO", Math.Max(0, before - after), item.Path, run.Success ? null : run.Combined.Trim());
            }
            finally
            {
                File.Delete(script);
            }
        }

        return result;
    }
}
