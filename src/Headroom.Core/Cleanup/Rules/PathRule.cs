using System.Text.Json.Serialization;
using Headroom.Core.Cleanup.Safety;
using Headroom.Core.SystemTools;

namespace Headroom.Core.Cleanup.Rules;

/// <summary>Como um alvo é tratado.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetMode
{
    /// <summary>Apaga o conteúdo da pasta (arquivos que passam nos filtros); a pasta em si fica.</summary>
    Contents,

    /// <summary>Apaga o próprio item encontrado (arquivo ou pasta, ex.: Windows.old, *.ipsw).</summary>
    Self,

    /// <summary>Cada filho imediato da pasta vira um item selecionável (ex.: backups do iPhone, modelos de IA).</summary>
    Children,
}

/// <summary>Alvo de uma regra declarativa (também usado nas regras JSON).</summary>
public sealed class PathTarget
{
    /// <summary>Caminho com variáveis (%LOCALAPPDATA%) e curingas em qualquer segmento.</summary>
    public string Path { get; set; } = string.Empty;

    public TargetMode Mode { get; set; } = TargetMode.Contents;

    /// <summary>Curinga do nome dos arquivos (Contents). Padrão "*".</summary>
    public string Pattern { get; set; } = "*";

    public bool Recursive { get; set; } = true;

    /// <summary>Só itens sem modificação há pelo menos X dias.</summary>
    public int MinAgeDays { get; set; }

    /// <summary>Só estas extensões (com ponto). Vazio = todas.</summary>
    public List<string>? Extensions { get; set; }

    /// <summary>Nomes (curingas) que nunca entram, ex.: "desktop.ini".</summary>
    public List<string>? Exclude { get; set; }

    /// <summary>Tamanho mínimo do arquivo (bytes).</summary>
    public long MinSize { get; set; }
}

/// <summary>Comando opcional executado depois da limpeza (regras JSON e embutidas).</summary>
public sealed class RuleCommand
{
    public string File { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>Definição declarativa de uma regra por caminho (no estilo CleanerML do BleachBit).</summary>
public sealed class PathRuleDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public CleanupCategory Category { get; set; } = CleanupCategory.Applications;
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;
    public bool RequiresAdmin { get; set; }
    public bool EnabledByDefault { get; set; } = true;
    public bool ItemsSelectedByDefault { get; set; } = true;
    public List<string> ProcessesToClose { get; set; } = new();
    public bool RequiresProcessesClosed { get; set; }
    public List<string> ServicesToStop { get; set; } = new();

    /// <summary>Se algum destes caminhos existir, a regra se aplica. Vazio = existe algum alvo.</summary>
    public List<string> Detect { get; set; } = new();

    public List<PathTarget> Targets { get; set; } = new();
    public bool AlwaysPermanent { get; set; }
    public bool AllowBrowserPrivacyData { get; set; }
    public RuleCommand? Command { get; set; }
}

/// <summary>
/// Regra genérica baseada em caminhos: expande variáveis/curingas, aplica filtros de nome, extensão,
/// idade e tamanho e agrupa pastas inteiras em um único item quando tudo dentro delas pode sair
/// (prévia compacta e exclusão rápida).
/// </summary>
public class PathRule : CleanupRuleBase
{
    private readonly PathRuleDefinition _definition;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<CleanupAnalysis, List<AllowedRoot>> _roots = new();

    public PathRule(PathRuleDefinition definition, string source = "Embutida")
        : base(definition.Id, definition.Category, definition.Name, definition.Description, definition.Risk)
    {
        _definition = definition;
        RequiresAdmin = definition.RequiresAdmin;
        EnabledByDefault = definition.EnabledByDefault;
        ItemsSelectedByDefault = definition.ItemsSelectedByDefault;
        ProcessesToClose = definition.ProcessesToClose;
        RequiresProcessesClosed = definition.RequiresProcessesClosed;
        AlwaysPermanent = definition.AlwaysPermanent;
        AllowBrowserPrivacyData = definition.AllowBrowserPrivacyData;
        Source = source;
    }

    public PathRuleDefinition Definition => _definition;

    public override bool IsApplicable(CleanupContext context)
    {
        if (_definition.Detect.Count > 0)
        {
            return _definition.Detect.Any(d => context.Paths.Expand(d).Count > 0);
        }

        return _definition.Targets.Any(t => context.Paths.Expand(t.Path).Count > 0);
    }

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken) =>
        Task.Run(() => Analyze(context, cancellationToken), cancellationToken);

    private CleanupAnalysis Analyze(CleanupContext context, CancellationToken ct)
    {
        var items = new List<CleanupItem>();
        var roots = new List<AllowedRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in _definition.Targets)
        {
            foreach (var basePath in context.Paths.Expand(target.Path))
            {
                ct.ThrowIfCancellationRequested();
                switch (target.Mode)
                {
                    case TargetMode.Self:
                        roots.Add(new AllowedRoot(basePath, AllowRootItself: true));
                        var self = DescribeSelf(basePath, target, ct);
                        if (self is not null && seen.Add(self.Path))
                        {
                            items.Add(self);
                        }

                        break;

                    case TargetMode.Children:
                        if (!Directory.Exists(basePath))
                        {
                            break;
                        }

                        roots.Add(new AllowedRoot(basePath));
                        foreach (var child in SafeEntries(basePath))
                        {
                            if ((child.Attributes & FileAttributes.ReparsePoint) != 0 ||
                                !PathExpander.MatchesWildcard(child.Name, target.Pattern) || IsExcluded(child.Name, target))
                            {
                                continue;
                            }

                            var described = DescribeSelf(child.FullName, target, ct);
                            if (described is not null && seen.Add(described.Path))
                            {
                                items.Add(described);
                            }
                        }

                        break;

                    default:
                        if (Directory.Exists(basePath))
                        {
                            roots.Add(new AllowedRoot(basePath));
                            foreach (var item in CollectContents(basePath, target, ct).Items)
                            {
                                if (seen.Add(item.Path))
                                {
                                    items.Add(item);
                                }
                            }
                        }
                        else if (File.Exists(basePath))
                        {
                            // Um arquivo indicado diretamente (ex.: %WINDIR%\MEMORY.DMP).
                            roots.Add(new AllowedRoot(basePath, AllowRootItself: true));
                            var self2 = DescribeSelf(basePath, target, ct);
                            if (self2 is not null && seen.Add(self2.Path))
                            {
                                items.Add(self2);
                            }
                        }

                        break;
                }
            }
        }

        foreach (var item in items)
        {
            item.IsSelected = ItemsSelectedByDefault;
        }

        string? note = null;
        if (RequiresAdmin && !context.IsAdministrator)
        {
            note = "Requer administrador: sem elevação, itens protegidos serão pulados.";
        }

        var analysis = new CleanupAnalysis(this, items.OrderByDescending(i => i.Size).ToList(), null, note);
        _roots.AddOrUpdate(analysis, roots);
        return analysis;
    }

    protected override IReadOnlyList<AllowedRoot> GetAllowedRoots(CleanupAnalysis analysis)
    {
        return _roots.TryGetValue(analysis, out var roots) ? roots : Array.Empty<AllowedRoot>();
    }

    public override async Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        List<string> stoppedServices = new();
        if (_definition.ServicesToStop.Count > 0)
        {
            progress?.Report(new CleanupProgress(Name, "Parando serviços: " + string.Join(", ", _definition.ServicesToStop), 0));
            stoppedServices = await ServiceHelper.StopAsync(_definition.ServicesToStop, cancellationToken).ConfigureAwait(false);
            execution.Log.Info($"Serviços parados: {string.Join(", ", stoppedServices)}");
        }

        try
        {
            var result = await base.CleanAsync(analysis, execution, progress, cancellationToken).ConfigureAwait(false);

            if (_definition.Command is { } command && !string.IsNullOrWhiteSpace(command.File))
            {
                string exe = Environment.ExpandEnvironmentVariables(command.File);
                string resolved = File.Exists(exe) ? exe : CommandRunner.FindOnPath(exe) ?? exe;
                var (file, args) = CommandRunner.Wrap(resolved, Environment.ExpandEnvironmentVariables(command.Arguments));
                var run = await CommandRunner.RunAsync(file, args, TimeSpan.FromSeconds(command.TimeoutSeconds),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                result.Output = run.Combined;
                execution.Log.Info($"Comando: {file} {args} → código {run.ExitCode}");
                if (!run.Success)
                {
                    result.Message = $"O comando terminou com código {run.ExitCode}.";
                }
            }

            return result;
        }
        finally
        {
            if (stoppedServices.Count > 0)
            {
                await ServiceHelper.StartAsync(stoppedServices).ConfigureAwait(false);
                execution.Log.Info($"Serviços reiniciados: {string.Join(", ", stoppedServices)}");
            }
        }
    }

    /// <summary>
    /// Coleta os itens de uma pasta. Se TUDO dentro de uma subpasta passa nos filtros, a subpasta inteira
    /// vira um único item; senão, só os arquivos que passam são listados (percurso paralelo em <see cref="ContentWalker"/>).
    /// </summary>
    private static ContentWalker.ContentResult CollectContents(string directory, PathTarget target, CancellationToken ct) =>
        ContentWalker.Collect(directory, target, ct);

    internal static bool IsExcluded(string name, PathTarget target) =>
        target.Exclude is { Count: > 0 } excludes && excludes.Any(x => PathExpander.MatchesWildcard(name, x));

    private static CleanupItem? DescribeSelf(string path, PathTarget target, CancellationToken ct)
    {
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    (target.MinAgeDays > 0 && info.LastWriteTime > DateTime.Now.AddDays(-target.MinAgeDays)) ||
                    info.Length < target.MinSize)
                {
                    return null;
                }

                return new CleanupItem(path, info.Length, false, info.LastWriteTime);
            }

            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return null;
                }

                var (size, files, newest) = MeasureDirectory(path, ct);
                if (target.MinAgeDays > 0 && newest > DateTime.Now.AddDays(-target.MinAgeDays))
                {
                    return null;
                }

                if (size < target.MinSize)
                {
                    return null;
                }

                return new CleanupItem(path, size, true, newest == DateTime.MinValue ? info.LastWriteTime : newest, files);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>Tamanho, arquivos e data mais recente de uma pasta (sem seguir links; percurso paralelo).</summary>
    public static (long Size, int Files, DateTime Newest) MeasureDirectory(string path, CancellationToken ct) =>
        ContentWalker.Measure(path, ct);

    private static IEnumerable<FileSystemInfo> SafeEntries(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<FileSystemInfo>();
        }
    }
}
