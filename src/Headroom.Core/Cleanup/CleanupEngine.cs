using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cleanup.Logging;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Formatting;
using Headroom.Core.SystemTools;

namespace Headroom.Core.Cleanup;

/// <summary>O que fazer quando um programa que deveria estar fechado está aberto.</summary>
public enum ProcessDecision
{
    CloseAndContinue,
    ContinueWithout,
    Skip,
}

public sealed class CleanupRunOptions
{
    public DeletionMode Mode { get; init; } = DeletionMode.RecycleBin;

    /// <summary>Criar ponto de restauração antes do primeiro item Avançado.</summary>
    public bool CreateRestorePoint { get; init; } = true;

    public bool IsAdministrator { get; init; }

    /// <summary>Lixeira alternativa (testes).</summary>
    public IRecycler? Recycler { get; init; }

    public string LogPrefix { get; init; } = "limpeza";

    /// <summary>Diretório dos logs (padrão: %LOCALAPPDATA%\Headroom\logs).</summary>
    public string? LogDirectory { get; init; }

    /// <summary>
    /// Chamado quando há programas abertos que a regra pede para fechar. Sem callback (modo silencioso):
    /// regras que exigem o programa fechado são puladas; as demais seguem pulando arquivos em uso.
    /// </summary>
    public Func<ICleanupRule, IReadOnlyList<RunningProcessInfo>, Task<ProcessDecision>>? OnProcessesRunning { get; init; }
}

/// <summary>Resumo final: GB liberados por categoria, itens pulados e erros.</summary>
public sealed class CleanupSummary
{
    public List<CleanupRunResult> Results { get; } = new();
    public Dictionary<CleanupCategory, long> BytesByCategory { get; } = new();
    public long TotalBytesFreed => BytesByCategory.Values.Sum();
    public long BytesMovedToRecycleBin => Results.Where(r => r.MovedToRecycleBin).Sum(r => r.BytesFreed);
    public int TotalSkipped => Results.Sum(r => r.Skipped);
    public int TotalErrors => Results.Sum(r => r.Errors);
    public List<string> SkippedRules { get; } = new();
    public string? LogPath { get; set; }
    public string? RestorePointMessage { get; set; }
    public bool Cancelled { get; set; }
    public TimeSpan Elapsed { get; set; }

    public string Headline =>
        Cancelled ? $"Limpeza cancelada. {ByteSize.Format(TotalBytesFreed)} já haviam sido processados."
        : BytesMovedToRecycleBin > 0
            ? $"{ByteSize.Format(TotalBytesFreed)} limpos ({ByteSize.Format(BytesMovedToRecycleBin)} foram para a Lixeira — o espaço só é liberado ao esvaziá-la)."
            : $"{ByteSize.Format(TotalBytesFreed)} liberados.";
}

/// <summary>
/// Orquestra análise e limpeza: analisa várias regras em paralelo, depois limpa em ordem segura
/// (Lixeira primeiro, depois Seguro → Revisar → Avançado), cria ponto de restauração antes de itens
/// avançados, trata programas abertos, grava log e consolida o resumo.
/// </summary>
public sealed class CleanupEngine
{
    private readonly CleanupContext _context;

    public CleanupEngine(CleanupContext context)
    {
        _context = context;
    }

    /// <param name="completed">Recebe cada análise assim que ela termina (para a interface mostrar aos poucos).</param>
    public async Task<IReadOnlyList<CleanupAnalysis>> AnalyzeAsync(IEnumerable<ICleanupRule> rules,
        IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default,
        IProgress<CleanupAnalysis>? completed = null)
    {
        var list = rules.ToList();
        var results = new CleanupAnalysis[list.Count];
        int done = 0;
        using var gate = new SemaphoreSlim(4);
        var tasks = list.Select(async (rule, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                progress?.Report(new CleanupProgress(rule.Name, "Analisando…", done / (double)Math.Max(1, list.Count)));
                results[index] = await rule.AnalyzeAsync(_context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results[index] = CleanupAnalysis.Unavailable(rule, "Erro na análise: " + ex.Message);
            }
            finally
            {
                int finished = Interlocked.Increment(ref done);
                progress?.Report(new CleanupProgress(rule.Name, "Concluído", finished / (double)Math.Max(1, list.Count)));
                if (results[index] is { } analysis)
                {
                    completed?.Report(analysis);
                }

                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>Ordem de execução: Lixeira, depois Seguro, Revisar e Avançado (estável dentro do mesmo nível).</summary>
    public static IReadOnlyList<CleanupAnalysis> ExecutionOrder(IEnumerable<CleanupAnalysis> analyses) =>
        analyses.Select((a, i) => (a, i))
            .OrderBy(x => x.a.Rule.Id == RecycleBinRule.RuleId ? 0 : 1)
            .ThenBy(x => x.a.Rule.Risk)
            .ThenBy(x => x.i)
            .Select(x => x.a)
            .ToList();

    public async Task<CleanupSummary> CleanAsync(IReadOnlyList<CleanupAnalysis> analyses, CleanupRunOptions options,
        IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var summary = new CleanupSummary();
        using var log = CleanupLog.Create(options.LogPrefix, options.LogDirectory);
        summary.LogPath = log.FilePath;
        var execution = new CleanupExecution(options.Mode, log, options.IsAdministrator, options.Recycler);

        var work = ExecutionOrder(analyses.Where(a => a.HasWork && !a.Rule.IsInformational)).ToList();
        log.Info($"Modo: {(options.Mode == DeletionMode.RecycleBin ? "mover para a Lixeira" : "exclusão permanente")}. Regras: {work.Count}.");

        if (options.CreateRestorePoint && work.Any(a => a.Rule.Risk == RiskLevel.Advanced))
        {
            if (options.IsAdministrator)
            {
                progress?.Report(new CleanupProgress("Ponto de restauração", "Criando ponto de restauração…", 0));
                var (ok, message) = await RestorePointService.CreateAsync("Headroom - antes da limpeza avançada", cancellationToken)
                    .ConfigureAwait(false);
                summary.RestorePointMessage = message;
                log.Info("Ponto de restauração: " + message);
            }
            else
            {
                summary.RestorePointMessage = "Ponto de restauração não criado (requer administrador).";
            }
        }

        for (int i = 0; i < work.Count; i++)
        {
            var analysis = work[i];
            var rule = analysis.Rule;
            if (cancellationToken.IsCancellationRequested)
            {
                summary.Cancelled = true;
                break;
            }

            progress?.Report(new CleanupProgress(rule.Name, "Preparando…", i / (double)work.Count));

            if (rule.ProcessesToClose.Count > 0)
            {
                var running = ProcessHelper.FindRunning(rule.ProcessesToClose);
                if (running.Count > 0)
                {
                    var decision = options.OnProcessesRunning is null
                        ? (rule.RequiresProcessesClosed ? ProcessDecision.Skip : ProcessDecision.ContinueWithout)
                        : await options.OnProcessesRunning(rule, running).ConfigureAwait(false);

                    if (decision == ProcessDecision.CloseAndContinue)
                    {
                        progress?.Report(new CleanupProgress(rule.Name, "Fechando " + string.Join(", ", running.Select(r => r.ExecutableName)), i / (double)work.Count));
                        await ProcessHelper.CloseAsync(rule.ProcessesToClose, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        log.Info($"Programas fechados para \"{rule.Name}\": {string.Join(", ", running.Select(r => r.ExecutableName))}");
                    }
                    else if (decision == ProcessDecision.Skip || rule.RequiresProcessesClosed)
                    {
                        summary.SkippedRules.Add($"{rule.Name}: {string.Join(", ", running.Select(r => r.ExecutableName))} está aberto.");
                        log.Info($"Regra pulada (programa aberto): {rule.Name}");
                        continue;
                    }
                }
            }

            CleanupRunResult result;
            try
            {
                var ruleProgress = progress is null
                    ? null
                    : new Progress<CleanupProgress>(p => progress.Report(p with { Fraction = (i + p.Fraction) / work.Count }));
                result = await rule.CleanAsync(analysis, execution, ruleProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                summary.Cancelled = true;
                log.Info("Cancelado pelo usuário.");
                break;
            }
            catch (Exception ex)
            {
                result = new CleanupRunResult(rule) { Success = false, Message = "Erro: " + ex.Message };
                log.Info($"Erro em \"{rule.Name}\": {ex}");
            }

            summary.Results.Add(result);
            summary.BytesByCategory[rule.Category] = summary.BytesByCategory.GetValueOrDefault(rule.Category) + result.BytesFreed;
            if (result.Deletion?.Cancelled == true)
            {
                summary.Cancelled = true;
                break;
            }
        }

        summary.Elapsed = DateTime.UtcNow - started;
        log.WriteRaw(string.Empty);
        log.Info($"RESUMO: {summary.Headline} Pulados: {summary.TotalSkipped}. Erros: {summary.TotalErrors}. Duração: {ByteSize.FormatDuration(summary.Elapsed)}.");
        foreach (var (category, bytes) in summary.BytesByCategory.Where(kv => kv.Value > 0))
        {
            log.Info($"  {CleanupCategoryInfo.DisplayName(category)}: {ByteSize.Format(bytes)}");
        }

        progress?.Report(new CleanupProgress("Concluído", summary.Headline, 1));
        return summary;
    }
}
