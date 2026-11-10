using System.Text.RegularExpressions;
using System.Xml.Linq;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Cleanup.Safety;
using Microsoft.Win32;

namespace Headroom.Core.Programs;

public enum LeftoverKind
{
    Folder,
    Shortcut,
    RegistryKey,
    RegistryValue,
    ScheduledTask,
    Service,
}

/// <summary>Alta = identificado com segurança (vem marcado); Média = parecido, revise antes (vem desmarcado).</summary>
public enum LeftoverConfidence
{
    High,
    Medium,
}

/// <summary>Um rastro deixado por um programa desinstalado.</summary>
public sealed class ProgramLeftover
{
    public required LeftoverKind Kind { get; init; }

    /// <summary>Pasta/arquivo, chave completa do registro (HKEY_...), caminho da tarefa (\Pasta\Nome) ou nome do serviço.</summary>
    public required string Path { get; init; }

    /// <summary>Para <see cref="LeftoverKind.RegistryValue"/>: o valor dentro da chave.</summary>
    public string? ValueName { get; init; }

    public long Size { get; init; }
    public required string Reason { get; init; }
    public LeftoverConfidence Confidence { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool IsSelected { get; set; }

    public string KindText => Kind switch
    {
        LeftoverKind.Folder => "Pasta",
        LeftoverKind.Shortcut => "Atalho",
        LeftoverKind.RegistryKey => "Chave do registro",
        LeftoverKind.RegistryValue => "Valor do registro",
        LeftoverKind.ScheduledTask => "Tarefa agendada",
        _ => "Serviço",
    };

    public string Display => ValueName is null ? Path : $"{Path}  →  {ValueName}";
}

/// <summary>
/// O que se sabe do programa ANTES de desinstalar (depois disso a entrada some do registro): nome, fabricante,
/// pasta, executáveis e os "nomes" com que ele costuma criar pastas e chaves.
/// </summary>
public sealed record ProgramFootprint(
    string Name,
    string? Publisher,
    string? InstallFolder,
    IReadOnlyList<string> Executables,
    string UninstallKey,
    IReadOnlyList<string> StrongTokens,
    IReadOnlyList<string> WeakTokens,
    string? PublisherToken)
{
    public static ProgramFootprint From(InstalledProgram program)
    {
        string? folder = program.ProbableFolder;
        var executables = new List<string>();
        if (folder is not null)
        {
            try
            {
                executables.AddRange(Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 2,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    })
                    .Select(System.IO.Path.GetFileName)
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(60));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var (strong, weak, publisher) = ProgramLeftoverScanner.BuildTokens(program.Name, program.Publisher, folder);
        return new ProgramFootprint(program.Name, program.Publisher, folder, executables, program.RegistryKey, strong, weak, publisher);
    }
}

/// <summary>
/// Procura os rastros de um programa depois da desinstalação, como os desinstaladores completos fazem:
/// pasta de instalação que ficou, pastas em Arquivos de Programas/ProgramData/AppData, atalhos, chaves e valores
/// do registro (Software, App Paths, Run, entrada de desinstalação), tarefas agendadas e serviços que apontam
/// para a pasta. Nada que outro programa instalado use é sugerido, e a lista negra de pastas protegidas vale.
/// </summary>
public static partial class ProgramLeftoverScanner
{
    /// <summary>Nomes genéricos demais para identificar um programa.</summary>
    private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "system", "program", "programs", "programfiles", "app", "apps", "application", "common",
        "commonfiles", "files", "data", "update", "updater", "setup", "install", "installer", "tools", "tool", "software",
        "free", "pro", "the", "and", "for", "driver", "drivers", "service", "services", "runtime", "sdk", "bin", "x64",
        "x86", "temp", "cache", "local", "roaming", "locallow", "users", "user", "default", "public", "packages", "plugins",
        "config", "settings", "logs", "lib", "client", "desktop", "studio", "player", "manager", "edition", "suite", "online",
        "version", "beta", "portable", "launcher", "helper", "support", "documents",
    };

    /// <summary>Chaves de primeiro nível em SOFTWARE que nunca são sugeridas.</summary>
    private static readonly HashSet<string> SystemKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "Wow6432Node", "Windows", "Clients", "RegisteredApplications", "ODBC",
        "Intel", "Khronos", "Partner", "WOW6432Node", "Mozilla", "Google", "Apple Inc.", "Oracle", "JavaSoft",
    };

    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    internal static (List<string> Strong, List<string> Weak, string? Publisher) BuildTokens(string name, string? publisher, string? folder)
    {
        var strong = new HashSet<string>(StringComparer.Ordinal);
        var weak = new HashSet<string>(StringComparer.Ordinal);

        string cleanName = Noise().Replace(name, " ").Trim();
        string? publisherToken = string.IsNullOrWhiteSpace(publisher)
            ? null
            : Normalize(CompanySuffix().Replace(publisher, " "));
        if (publisherToken is { Length: < 3 } || (publisherToken is not null && GenericTokens.Contains(publisherToken)))
        {
            publisherToken = null;
        }

        AddStrong(Normalize(cleanName));

        // Nome sem o fabricante na frente ("Microsoft Teams" → "teams", "Mozilla Thunderbird" → "thunderbird").
        if (!string.IsNullOrWhiteSpace(publisher))
        {
            string firstPublisherWord = publisher.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (cleanName.StartsWith(firstPublisherWord + " ", StringComparison.OrdinalIgnoreCase))
            {
                AddStrong(Normalize(cleanName[(firstPublisherWord.Length + 1)..]));
            }
        }

        // Pasta de instalação ("C:\Program Files\VideoLAN\VLC" → "vlc").
        if (folder is not null)
        {
            AddStrong(Normalize(System.IO.Path.GetFileName(folder.TrimEnd('\\'))));
        }

        // Primeira palavra ("Discord PTB" → "discord") só como indício fraco.
        string first = Normalize(cleanName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty);
        if (first.Length >= 4 && !GenericTokens.Contains(first) && !strong.Contains(first))
        {
            weak.Add(first);
        }

        return (strong.ToList(), weak.ToList(), publisherToken);

        void AddStrong(string token)
        {
            if (token.Length >= 3 && !GenericTokens.Contains(token))
            {
                strong.Add(token);
            }
        }
    }

    /// <summary>Verdadeiro se a entrada de desinstalação do programa ainda existe (desinstalação não concluída).</summary>
    public static bool IsStillRegistered(ProgramFootprint footprint)
    {
        try
        {
            var (hive, sub) = SplitKey(footprint.UninstallKey);
            using (hive)
            {
                using var key = hive.OpenSubKey(sub);
                return key is not null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Procura os rastros. <paramref name="stillInstalled"/> é a lista atual de programas (sem o que saiu);
    /// tudo o que eles usam fica de fora.
    /// </summary>
    public static Task<List<ProgramLeftover>> FindAsync(ProgramFootprint footprint, IReadOnlyList<InstalledProgram> stillInstalled,
        CancellationToken cancellationToken = default) =>
        FindAsync(footprint, stillInstalled, LeftoverSearchLocations.Real(), cancellationToken);

    internal static Task<List<ProgramLeftover>> FindAsync(ProgramFootprint footprint, IReadOnlyList<InstalledProgram> stillInstalled,
        LeftoverSearchLocations locations, CancellationToken cancellationToken = default) =>
        Task.Run(() => Find(footprint, stillInstalled, locations, cancellationToken), cancellationToken);

    private static List<ProgramLeftover> Find(ProgramFootprint footprint, IReadOnlyList<InstalledProgram> stillInstalled,
        LeftoverSearchLocations locations, CancellationToken ct)
    {
        var others = stillInstalled.Where(p => !string.Equals(p.RegistryKey, footprint.UninstallKey, StringComparison.OrdinalIgnoreCase)).ToList();
        var context = new SearchContext(footprint, others);
        var found = new List<ProgramLeftover>();

        FindFolders(context, locations, found, ct);
        FindShortcuts(context, locations, found, ct);
        FindRegistry(context, locations, found, ct);
        if (locations.IncludeTasks)
        {
            FindScheduledTasks(context, found, ct);
        }

        if (locations.IncludeServices)
        {
            FindServices(context, found, ct);
        }

        // Itens repetidos (a mesma pasta achada por dois caminhos) e itens dentro de uma pasta já listada.
        var result = new List<ProgramLeftover>();
        foreach (var item in found.OrderBy(f => f.Confidence).ThenBy(f => f.Path.Length))
        {
            bool duplicate = result.Any(r => r.Kind == item.Kind && r.ValueName == item.ValueName &&
                                             (string.Equals(r.Path, item.Path, StringComparison.OrdinalIgnoreCase) ||
                                              (item.Kind is LeftoverKind.Folder or LeftoverKind.Shortcut or LeftoverKind.RegistryKey &&
                                               item.Path.StartsWith(r.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))));
            if (!duplicate)
            {
                item.IsSelected = item.Confidence == LeftoverConfidence.High;
                result.Add(item);
            }
        }

        return result.OrderBy(r => r.Kind).ThenByDescending(r => r.Size).ToList();
    }

    // ---------------------------------------------------------------- Pastas

    private static void FindFolders(SearchContext c, LeftoverSearchLocations locations, List<ProgramLeftover> found, CancellationToken ct)
    {
        // A própria pasta de instalação, se o desinstalador a deixou para trás.
        if (c.Footprint.InstallFolder is { } install && Directory.Exists(install) && c.IsFolderFree(install))
        {
            AddFolder(found, install, LeftoverConfidence.High, "Pasta de instalação que o desinstalador deixou para trás.", ct);
        }

        foreach (var (root, label) in locations.FolderRoots.Where(r => !string.IsNullOrEmpty(r.Path) && Directory.Exists(r.Path))
                     .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dir in SafeDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                string token = Normalize(dir.Name);
                if (c.IsStrong(token))
                {
                    if (c.IsFolderFree(dir.FullName))
                    {
                        AddFolder(found, dir.FullName, LeftoverConfidence.High, $"Pasta com o nome do programa em {label}.", ct);
                    }
                }
                else if (c.Footprint.PublisherToken is { } publisher && token == publisher && !c.TokenUsedByOthers(token))
                {
                    // Pasta do fabricante: só o que tem o nome do programa; a pasta inteira, se não sobrar mais nada nela.
                    var children = SafeDirectories(dir.FullName).ToList();
                    var matching = children.Where(ch => c.IsStrong(Normalize(ch.Name)) && c.IsFolderFree(ch.FullName)).ToList();
                    bool onlyProgram = matching.Count > 0 && matching.Count == children.Count && !SafeHasFiles(dir.FullName);
                    if (onlyProgram && c.IsFolderFree(dir.FullName))
                    {
                        AddFolder(found, dir.FullName, LeftoverConfidence.High,
                            $"Pasta do fabricante em {label} que só contém o programa desinstalado.", ct);
                    }
                    else
                    {
                        foreach (var child in matching)
                        {
                            AddFolder(found, child.FullName, LeftoverConfidence.High, $"Pasta do programa dentro da pasta do fabricante em {label}.", ct);
                        }
                    }
                }
                else if (c.IsWeak(token) && c.IsFolderFree(dir.FullName))
                {
                    AddFolder(found, dir.FullName, LeftoverConfidence.Medium,
                        $"Nome parecido com o do programa em {label}. Confira se não é de outro programa antes de marcar.", ct);
                }
            }
        }
    }

    private static void AddFolder(List<ProgramLeftover> found, string path, LeftoverConfidence confidence, string reason, CancellationToken ct)
    {
        if (DeletionGuard.Default.Check(path).IsProtected)
        {
            return;
        }

        var (size, _, _) = PathRule.MeasureDirectory(path, ct);
        found.Add(new ProgramLeftover
        {
            Kind = LeftoverKind.Folder,
            Path = path,
            Size = size,
            Reason = reason,
            Confidence = confidence,
            RequiresAdmin = !IsUserPath(path),
        });
    }

    // ---------------------------------------------------------------- Atalhos

    private static void FindShortcuts(SearchContext c, LeftoverSearchLocations locations, List<ProgramLeftover> found, CancellationToken ct)
    {
        var roots = locations.ShortcutRoots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var link in links)
            {
                ct.ThrowIfCancellationRequested();
                string? target = BrokenShortcutsRule.ResolveShortcut(link);
                bool pointsInside = target is not null && c.Footprint.InstallFolder is { } install &&
                                    ProtectedPaths.IsSameOrInside(target, install);
                bool brokenWithName = (target is null || (System.IO.Path.IsPathFullyQualified(target) && !File.Exists(target) && !Directory.Exists(target))) &&
                                      c.IsStrong(Normalize(System.IO.Path.GetFileNameWithoutExtension(link)));
                bool exeMatch = target is not null && !File.Exists(target) &&
                                c.Footprint.Executables.Contains(System.IO.Path.GetFileName(target), StringComparer.OrdinalIgnoreCase);
                if (pointsInside || brokenWithName || exeMatch)
                {
                    found.Add(new ProgramLeftover
                    {
                        Kind = LeftoverKind.Shortcut,
                        Path = link,
                        Size = SafeLength(link),
                        Reason = pointsInside
                            ? "Atalho para a pasta do programa desinstalado."
                            : "Atalho com o nome do programa que aponta para um arquivo que não existe mais.",
                        Confidence = LeftoverConfidence.High,
                        RequiresAdmin = !IsUserPath(link),
                    });
                }
            }
        }
    }

    // ---------------------------------------------------------------- Registro

    private static void FindRegistry(SearchContext c, LeftoverSearchLocations locations, List<ProgramLeftover> found, CancellationToken ct)
    {
        foreach (var (hive, path) in locations.SoftwareKeys)
        {
            ct.ThrowIfCancellationRequested();
            using var root = OpenKey(hive, path);
            if (root is null)
            {
                continue;
            }

            foreach (string name in SafeSubKeyNames(root))
            {
                if (SystemKeys.Contains(name))
                {
                    continue;
                }

                string token = Normalize(name);
                string full = $@"{HiveName(hive)}\{path}\{name}";
                if (c.IsStrong(token) && !c.TokenUsedByOthers(token))
                {
                    AddKey(found, full, LeftoverConfidence.High, "Chave de configurações com o nome do programa.", hive);
                }
                else if (c.Footprint.PublisherToken is { } publisher && token == publisher && !c.TokenUsedByOthers(token))
                {
                    using var publisherKey = root.OpenSubKey(name);
                    var children = publisherKey is null ? new List<string>() : SafeSubKeyNames(publisherKey).ToList();
                    var matching = children.Where(ch => c.IsStrong(Normalize(ch))).ToList();
                    if (matching.Count > 0 && matching.Count == children.Count && (publisherKey?.ValueCount ?? 0) == 0)
                    {
                        AddKey(found, full, LeftoverConfidence.High, "Chave do fabricante que só contém o programa desinstalado.", hive);
                    }
                    else
                    {
                        foreach (var child in matching)
                        {
                            AddKey(found, $@"{full}\{child}", LeftoverConfidence.High, "Chave do programa dentro da chave do fabricante.", hive);
                        }
                    }
                }
                else if (c.IsWeak(token) && !c.TokenUsedByOthers(token))
                {
                    AddKey(found, full, LeftoverConfidence.Medium, "Chave com nome parecido com o do programa. Confira antes de marcar.", hive);
                }
            }
        }

        // App Paths e "Abrir com" (Applications\programa.exe) dos executáveis do programa.
        foreach (string exe in locations.IncludeAppPaths ? c.Footprint.Executables : Array.Empty<string>())
        {
            if (c.ExecutableUsedByOthers(exe))
            {
                continue;
            }

            foreach (var (hive, path) in new[]
                     {
                         (RegistryHive.CurrentUser, $@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}"),
                         (RegistryHive.LocalMachine, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}"),
                         (RegistryHive.LocalMachine, $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\{exe}"),
                         (RegistryHive.CurrentUser, $@"Software\Classes\Applications\{exe}"),
                     })
            {
                using var key = OpenKey(hive, path);
                if (key is not null)
                {
                    AddKey(found, $@"{HiveName(hive)}\{path}", LeftoverConfidence.High,
                        $"Registro do executável {exe} (atalho de execução ou \"Abrir com\").", hive);
                }
            }
        }

        // Itens de inicialização (Run) que apontam para a pasta ou para os executáveis do programa.
        foreach (var (hive, path) in locations.RunKeys)
        {
            using var key = OpenKey(hive, path);
            if (key is null)
            {
                continue;
            }

            foreach (string valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is string command && c.CommandBelongsToProgram(command))
                {
                    found.Add(new ProgramLeftover
                    {
                        Kind = LeftoverKind.RegistryValue,
                        Path = $@"{HiveName(hive)}\{path}",
                        ValueName = valueName,
                        Reason = "Item de inicialização que abre o programa desinstalado.",
                        Confidence = LeftoverConfidence.High,
                        RequiresAdmin = hive == RegistryHive.LocalMachine,
                    });
                }
            }
        }

        // A própria entrada de desinstalação, se ficou (aparece em "Aplicativos" sem conseguir desinstalar).
        if (locations.IncludeUninstallEntry && IsStillRegistered(c.Footprint) &&
            (c.Footprint.InstallFolder is null || !Directory.Exists(c.Footprint.InstallFolder)))
        {
            found.Add(new ProgramLeftover
            {
                Kind = LeftoverKind.RegistryKey,
                Path = c.Footprint.UninstallKey,
                Reason = "Entrada de desinstalação que ficou no registro sem o programa.",
                Confidence = LeftoverConfidence.High,
                RequiresAdmin = c.Footprint.UninstallKey.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    private static void AddKey(List<ProgramLeftover> found, string fullPath, LeftoverConfidence confidence, string reason, RegistryHive hive) =>
        found.Add(new ProgramLeftover
        {
            Kind = LeftoverKind.RegistryKey,
            Path = fullPath,
            Reason = reason,
            Confidence = confidence,
            RequiresAdmin = hive == RegistryHive.LocalMachine,
        });

    // ---------------------------------------------------------------- Tarefas agendadas

    private static void FindScheduledTasks(SearchContext c, List<ProgramLeftover> found, CancellationToken ct)
    {
        string tasksRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
        if (!Directory.Exists(tasksRoot))
        {
            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(tasksRoot, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).Where(f => !f.StartsWith(System.IO.Path.Combine(tasksRoot, "Microsoft") + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string taskName = file[tasksRoot.Length..];
            bool nameMatches = c.IsStrong(Normalize(System.IO.Path.GetFileName(file))) ||
                               c.Footprint.StrongTokens.Any(t => t.Length >= 5 && Normalize(System.IO.Path.GetFileName(file)).StartsWith(t, StringComparison.Ordinal));
            bool commandMatches = false;
            try
            {
                var doc = XDocument.Load(file);
                commandMatches = doc.Descendants().Where(e => e.Name.LocalName == "Command")
                    .Any(e => c.CommandBelongsToProgram(e.Value));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
            }

            if (commandMatches || nameMatches)
            {
                found.Add(new ProgramLeftover
                {
                    Kind = LeftoverKind.ScheduledTask,
                    Path = taskName,
                    Reason = commandMatches
                        ? "Tarefa agendada que executa o programa desinstalado (ex.: atualizador)."
                        : "Tarefa agendada com o nome do programa.",
                    Confidence = commandMatches ? LeftoverConfidence.High : LeftoverConfidence.Medium,
                    RequiresAdmin = true,
                });
            }
        }
    }

    // ---------------------------------------------------------------- Serviços

    private static void FindServices(SearchContext c, List<ProgramLeftover> found, CancellationToken ct)
    {
        using var services = OpenKey(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services");
        if (services is null)
        {
            return;
        }

        foreach (string name in SafeSubKeyNames(services))
        {
            ct.ThrowIfCancellationRequested();
            using var key = services.OpenSubKey(name);
            if (key?.GetValue("ImagePath") is not string image || !c.CommandBelongsToProgram(image))
            {
                continue;
            }

            string? exe = Optimization.StartupManager.ExtractExecutable(Environment.ExpandEnvironmentVariables(image));
            bool missing = exe is not null && System.IO.Path.IsPathFullyQualified(exe) && !File.Exists(exe);
            found.Add(new ProgramLeftover
            {
                Kind = LeftoverKind.Service,
                Path = name,
                Reason = missing
                    ? $"Serviço do programa cujo executável não existe mais ({exe})."
                    : $"Serviço que roda a partir da pasta do programa ({exe}).",
                Confidence = missing ? LeftoverConfidence.High : LeftoverConfidence.Medium,
                RequiresAdmin = true,
            });
        }
    }

    // ---------------------------------------------------------------- Apoio

    /// <summary>Onde procurar. <see cref="Real"/> são os locais reais; os testes usam pastas e chaves temporárias.</summary>
    internal sealed class LeftoverSearchLocations
    {
        public required IReadOnlyList<(string Path, string Label)> FolderRoots { get; init; }
        public required IReadOnlyList<string> ShortcutRoots { get; init; }
        public required IReadOnlyList<(RegistryHive Hive, string Path)> SoftwareKeys { get; init; }
        public required IReadOnlyList<(RegistryHive Hive, string Path)> RunKeys { get; init; }
        public bool IncludeAppPaths { get; init; } = true;
        public bool IncludeTasks { get; init; } = true;
        public bool IncludeServices { get; init; } = true;
        public bool IncludeUninstallEntry { get; init; } = true;

        public static LeftoverSearchLocations Real()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new LeftoverSearchLocations
            {
                FolderRoots = new (string, string)[]
                {
                    (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Arquivos de Programas"),
                    (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Arquivos de Programas (x86)"),
                    (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData"),
                    (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData\\Roaming (configurações)"),
                    (local, "AppData\\Local"),
                    (System.IO.Path.Combine(local, "Programs"), "AppData\\Local\\Programs"),
                    (System.IO.Path.Combine(profile, "AppData", "LocalLow"), "AppData\\LocalLow"),
                    (Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Menu Iniciar"),
                    (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Menu Iniciar (todos os usuários)"),
                },
                ShortcutRoots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                },
                SoftwareKeys = new[]
                {
                    (RegistryHive.CurrentUser, "Software"),
                    (RegistryHive.LocalMachine, "SOFTWARE"),
                    (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node"),
                },
                RunKeys = new[]
                {
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
                    (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                    (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
                },
            };
        }
    }

    private sealed class SearchContext
    {
        private readonly HashSet<string> _strong;
        private readonly HashSet<string> _weak;
        private readonly HashSet<string> _otherTokens = new(StringComparer.Ordinal);
        private readonly List<string> _otherFolders = new();
        private readonly HashSet<string> _otherExecutables = new(StringComparer.OrdinalIgnoreCase);

        public SearchContext(ProgramFootprint footprint, IReadOnlyList<InstalledProgram> others)
        {
            Footprint = footprint;
            _strong = new HashSet<string>(footprint.StrongTokens, StringComparer.Ordinal);
            _weak = new HashSet<string>(footprint.WeakTokens, StringComparer.Ordinal);
            foreach (var program in others)
            {
                var (strong, weak, publisher) = BuildTokens(program.Name, program.Publisher, program.ProbableFolder);
                _otherTokens.UnionWith(strong);
                _otherTokens.UnionWith(weak);
                if (publisher is not null)
                {
                    _otherTokens.Add(publisher);
                }

                if (program.ProbableFolder is { } folder)
                {
                    _otherFolders.Add(folder);
                }

                if (Optimization.StartupManager.ExtractExecutable(program.DisplayIcon?.Split(',')[0] ?? string.Empty) is { } icon)
                {
                    _otherExecutables.Add(System.IO.Path.GetFileName(icon));
                }
            }
        }

        public ProgramFootprint Footprint { get; }

        public bool IsStrong(string token) => token.Length >= 3 && _strong.Contains(token) && !_otherTokens.Contains(token);

        public bool IsWeak(string token) => token.Length >= 4 && _weak.Contains(token) && !_otherTokens.Contains(token);

        public bool TokenUsedByOthers(string token) => _otherTokens.Contains(token);

        public bool ExecutableUsedByOthers(string exe) => _otherExecutables.Contains(exe);

        /// <summary>A pasta não é (nem contém, nem está dentro de) a pasta de outro programa instalado.</summary>
        public bool IsFolderFree(string folder) =>
            !_otherFolders.Any(other => ProtectedPaths.IsSameOrInside(other, folder) || ProtectedPaths.IsSameOrInside(folder, other));

        /// <summary>A linha de comando executa algo da pasta do programa (ou um executável dele que não existe mais).</summary>
        public bool CommandBelongsToProgram(string command)
        {
            string expanded = Environment.ExpandEnvironmentVariables(command);
            if (Footprint.InstallFolder is { } install &&
                expanded.Contains(install.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string? exe = Optimization.StartupManager.ExtractExecutable(expanded);
            return exe is not null && System.IO.Path.IsPathFullyQualified(exe) && !File.Exists(exe) &&
                   Footprint.Executables.Contains(System.IO.Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static (RegistryKey Hive, string SubKey) SplitKey(string fullPath)
    {
        int slash = fullPath.IndexOf('\\');
        string hive = fullPath[..slash];
        string sub = fullPath[(slash + 1)..];
        return hive.ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" => (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64), sub),
            "HKEY_LOCAL_MACHINE" => (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), sub),
            _ => throw new ArgumentException("Hive não suportado: " + hive),
        };
    }

    private static RegistryKey? OpenKey(RegistryHive hive, string path)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            return baseKey.OpenSubKey(path);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static string HiveName(RegistryHive hive) => hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";

    private static IEnumerable<DirectoryInfo> SafeDirectories(string root)
    {
        try
        {
            return new DirectoryInfo(root).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true })
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<DirectoryInfo>();
        }
    }

    private static bool SafeHasFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool IsUserPath(string path) =>
        ProtectedPaths.IsSameOrInside(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Minúsculas, só letras e números ("Corsair iCUE 4" → "corsairicue4").</summary>
    internal static string Normalize(string text) => NonAlnum().Replace(text.ToLowerInvariant(), string.Empty);

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    /// <summary>Versões, arquitetura e sufixos que não fazem parte do nome ("7-Zip 23.01 (x64)" → "7-Zip").</summary>
    [GeneratedRegex(@"\(.*?\)|\b\d+(\.\d+)+\b|\b(x64|x86|64-bit|32-bit|64 bits|32 bits|amd64|version|versão)\b|\s+v?\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\b(corporation|corp|inc|incorporated|ltd|limited|llc|gmbh|co|company|s\.?a\.?|ag|bv|srl|ltda|technologies|software)\b\.?|,", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();
}
