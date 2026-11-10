using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cleanup.Logging;
using Headroom.Core.Cleanup.Safety;
using Headroom.Core.Scanning;
using Headroom.Core.Settings;

namespace Headroom.Core.Cleanup;

/// <summary>Contexto disponível para as regras durante a análise.</summary>
public sealed class CleanupContext
{
    public CleanupContext(AppSettings settings, bool isAdministrator, ScanResult? lastScan = null, PathExpander? paths = null)
    {
        Settings = settings;
        IsAdministrator = isAdministrator;
        LastScan = lastScan;
        Paths = paths ?? new PathExpander();
    }

    public AppSettings Settings { get; }
    public bool IsAdministrator { get; }
    public ScanResult? LastScan { get; }
    public PathExpander Paths { get; }
}

/// <summary>Contexto da execução (limpeza propriamente dita).</summary>
public sealed class CleanupExecution
{
    public CleanupExecution(DeletionMode mode, CleanupLog log, bool isAdministrator, IRecycler? recycler = null,
        DeletionGuard? guard = null)
    {
        Mode = mode;
        Log = log;
        IsAdministrator = isAdministrator;
        Deleter = new FileDeleter(guard, recycler, log);
    }

    /// <summary>Lixeira (padrão) ou exclusão permanente (confirmada explicitamente pelo usuário).</summary>
    public DeletionMode Mode { get; }
    public CleanupLog Log { get; }
    public bool IsAdministrator { get; }
    public FileDeleter Deleter { get; }
}

/// <summary>
/// Uma limpeza independente. Implementações em C# (catálogo embutido) ou declarativas em JSON.
/// Fluxo obrigatório: <see cref="AnalyzeAsync"/> (prévia) → usuário revisa → <see cref="CleanAsync"/>.
/// </summary>
public interface ICleanupRule
{
    string Id { get; }
    CleanupCategory Category { get; }
    string Name { get; }
    string Description { get; }
    RiskLevel Risk { get; }
    bool RequiresAdmin { get; }

    /// <summary>Marcada por padrão no perfil "Rápida"/primeiro uso.</summary>
    bool EnabledByDefault { get; }

    /// <summary>Itens vêm marcados na prévia? (false = o usuário escolhe item a item).</summary>
    bool ItemsSelectedByDefault { get; }

    /// <summary>Processos que devem estar fechados (ex.: chrome.exe).</summary>
    IReadOnlyList<string> ProcessesToClose { get; }

    /// <summary>Se true, a regra é pulada quando o app continua aberto; se false, só arquivos em uso são pulados.</summary>
    bool RequiresProcessesClosed { get; }

    /// <summary>Sempre exclui de forma permanente (itens grandes demais para a Lixeira ou comandos).</summary>
    bool AlwaysPermanent { get; }

    /// <summary>Só informativa (ex.: tamanho do pagefile.sys); não tem ação de limpeza.</summary>
    bool IsInformational { get; }

    /// <summary>Origem da regra ("Embutida" ou caminho do arquivo JSON).</summary>
    string Source { get; }

    /// <summary>Detecta se o app/recurso existe neste PC (regras não aplicáveis ficam ocultas).</summary>
    bool IsApplicable(CleanupContext context);

    Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken);

    Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Base com metadados e a limpeza padrão (apagar os itens selecionados com segurança).</summary>
public abstract class CleanupRuleBase : ICleanupRule
{
    protected CleanupRuleBase(string id, CleanupCategory category, string name, string description, RiskLevel risk)
    {
        Id = id;
        Category = category;
        Name = name;
        Description = description;
        Risk = risk;
    }

    public string Id { get; }
    public CleanupCategory Category { get; }
    public string Name { get; }
    public string Description { get; }
    public RiskLevel Risk { get; }
    public bool RequiresAdmin { get; init; }
    public bool EnabledByDefault { get; init; } = true;
    public bool ItemsSelectedByDefault { get; init; } = true;
    public IReadOnlyList<string> ProcessesToClose { get; init; } = Array.Empty<string>();
    public bool RequiresProcessesClosed { get; init; }
    public bool AlwaysPermanent { get; init; }
    public bool IsInformational { get; init; }
    public bool AllowBrowserPrivacyData { get; init; }
    public string Source { get; init; } = "Embutida";

    public virtual bool IsApplicable(CleanupContext context) => true;

    public abstract Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken);

    /// <summary>Raízes onde esta regra pode apagar (usadas pelo DeletionGuard).</summary>
    protected virtual IReadOnlyList<AllowedRoot> GetAllowedRoots(CleanupAnalysis analysis) =>
        analysis.Items.Select(i => new AllowedRoot(i.Path, AllowRootItself: true)).ToList();

    public virtual async Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new CleanupRunResult(this);
        var selected = analysis.Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            result.Message = "Nenhum item selecionado.";
            return result;
        }

        var mode = AlwaysPermanent ? DeletionMode.Permanent : execution.Mode;
        var targets = selected.Select(i => new DeletionTarget(i.Path, i.IsDirectory, i.Size)).ToList();
        var deletionProgress = progress is null
            ? null
            : new Progress<DeletionProgress>(p => progress.Report(new CleanupProgress(Name, p.CurrentPath,
                p.TotalItems == 0 ? 1 : p.ProcessedItems / (double)p.TotalItems)));

        var deletion = await execution.Deleter.DeleteAsync(targets, new DeletionOptions
        {
            Mode = mode,
            AllowedRoots = GetAllowedRoots(analysis),
            AllowBrowserPrivacyData = AllowBrowserPrivacyData,
            LogLabel = $"{Name} [{Id}]",
        }, deletionProgress, cancellationToken).ConfigureAwait(false);

        result.Deletion = deletion;
        result.BytesFreed = deletion.BytesDeleted;
        result.MovedToRecycleBin = mode == DeletionMode.RecycleBin;
        return result;
    }

    public override string ToString() => $"{Id}: {Name}";
}
