using System.Text;
using Headroom.Core.Formatting;
using Headroom.Core.SystemTools;

namespace Headroom.Core.Cleanup.Rules;

/// <summary>Um passo de comando. "{exe}" no arquivo é trocado pelo executável detectado da regra.</summary>
public sealed record CommandStep(string File, string Arguments, bool PowerShell = false, int TimeoutMinutes = 60,
    Encoding? Encoding = null, bool IgnoreExitCode = false);

/// <summary>
/// Regra que executa comandos do sistema ou de ferramentas (DISM, powercfg, docker, npm, conda...).
/// A estimativa vem do tamanho de pastas conhecidas ou de um estimador próprio; o espaço liberado
/// é medido pela diferença de espaço livre no disco antes/depois.
/// </summary>
public class CommandRule : CleanupRuleBase
{
    public CommandRule(string id, CleanupCategory category, string name, string description, RiskLevel risk)
        : base(id, category, name, description, risk)
    {
        AlwaysPermanent = true;
    }

    /// <summary>Executável necessário (nome no PATH ou caminho com variáveis). Null = ferramenta do Windows.</summary>
    public string? RequiredExecutable { get; init; }

    /// <summary>Caminhos alternativos onde procurar o executável (ex.: %USERPROFILE%\miniconda3\Scripts\conda.exe).</summary>
    public IReadOnlyList<string> ExecutableFallbacks { get; init; } = Array.Empty<string>();

    public IReadOnlyList<CommandStep> Steps { get; init; } = Array.Empty<CommandStep>();

    /// <summary>Pastas cujo tamanho é a estimativa do que será liberado.</summary>
    public IReadOnlyList<string> EstimatePaths { get; init; } = Array.Empty<string>();

    /// <summary>Estimador personalizado (bytes, observação). Retornar bytes = -1 indica "não aplicável agora".</summary>
    public Func<CleanupContext, CancellationToken, Task<(long Bytes, string? Note)>>? Estimator { get; init; }

    /// <summary>Texto que explica o que será executado.</summary>
    public string? ActionText { get; init; }

    /// <summary>Disco onde medir o espaço liberado (padrão: disco do sistema ou da primeira pasta estimada).</summary>
    public string? MeasureDrive { get; init; }

    /// <summary>Caminho com variáveis que precisa existir para a regra aparecer.</summary>
    public string? DetectPath { get; init; }

    public string? ResolveExecutable(CleanupContext? context)
    {
        if (RequiredExecutable is null)
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(RequiredExecutable);
        if (Path.IsPathFullyQualified(expanded) && File.Exists(expanded))
        {
            return expanded;
        }

        var found = CommandRunner.FindOnPath(expanded);
        if (found is not null)
        {
            return found;
        }

        foreach (var fallback in ExecutableFallbacks)
        {
            foreach (var candidate in (context?.Paths ?? new PathExpander()).Expand(fallback))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public override bool IsApplicable(CleanupContext context)
    {
        if (DetectPath is not null && context.Paths.Expand(DetectPath).Count == 0)
        {
            return false;
        }

        return RequiredExecutable is null || ResolveExecutable(context) is not null;
    }

    public override async Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        if (RequiresAdmin && !context.IsAdministrator)
        {
            return CleanupAnalysis.Unavailable(this, "Requer administrador. Use \"Reiniciar como administrador\".");
        }

        long bytes = 0;
        string? note = ActionText;
        if (Estimator is not null)
        {
            var (estimate, estimateNote) = await Estimator(context, cancellationToken).ConfigureAwait(false);
            if (estimate < 0)
            {
                return CleanupAnalysis.Unavailable(this, estimateNote ?? "Nada a fazer.");
            }

            bytes = estimate;
            note = string.IsNullOrEmpty(estimateNote) ? note : (note is null ? estimateNote : note + " " + estimateNote);
        }
        else
        {
            foreach (var pattern in EstimatePaths)
            {
                foreach (var path in context.Paths.Expand(pattern))
                {
                    bytes += await Task.Run(() => DiskMeasure.DirectorySize(path, cancellationToken), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        return CleanupAnalysis.Action(this, bytes, note ?? "Executa um comando do sistema.");
    }

    public override async Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new CleanupRunResult(this);
        string? exe = ResolveExecutable(null);
        string drive = MeasureDrive ?? DiskMeasure.SystemDrive;
        long freeBefore = DiskMeasure.FreeSpace(drive);
        var output = new StringBuilder();
        execution.Log.Section($"{Name} [{Id}] (comando)");

        for (int i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            string file = step.File.Replace("{exe}", exe ?? step.File, StringComparison.Ordinal);
            string args = Environment.ExpandEnvironmentVariables(step.Arguments);
            progress?.Report(new CleanupProgress(Name, step.PowerShell ? "PowerShell" : $"{Path.GetFileName(file)} {args}",
                i / (double)Math.Max(1, Steps.Count)));

            var lines = new Progress<string>(line => progress?.Report(new CleanupProgress(Name, line, i / (double)Math.Max(1, Steps.Count))));
            CommandResult run;
            if (step.PowerShell)
            {
                run = await CommandRunner.RunPowerShellAsync(args, TimeSpan.FromMinutes(step.TimeoutMinutes), lines, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var (wrappedFile, wrappedArgs) = CommandRunner.Wrap(Environment.ExpandEnvironmentVariables(file), args);
                run = await CommandRunner.RunAsync(wrappedFile, wrappedArgs, TimeSpan.FromMinutes(step.TimeoutMinutes), lines,
                    step.Encoding, cancellationToken).ConfigureAwait(false);
            }

            output.AppendLine(run.Combined.Trim());
            execution.Log.Info($"Executado: {(step.PowerShell ? "powershell" : file)} {(step.PowerShell ? string.Empty : args)} → código {run.ExitCode}");
            foreach (var line in run.Combined.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(50))
            {
                execution.Log.WriteRaw("    " + line.TrimEnd());
            }

            if (!run.Success && !step.IgnoreExitCode)
            {
                result.Success = false;
                result.Message = run.TimedOut
                    ? "O comando demorou demais e foi interrompido."
                    : $"O comando terminou com código {run.ExitCode}. Veja o log para detalhes.";
                break;
            }
        }

        long freeAfter = DiskMeasure.FreeSpace(drive);
        result.BytesFreed = Math.Max(0, freeAfter - freeBefore);
        result.Output = output.ToString();
        execution.Log.Info($"Espaço livre em {drive}: antes {ByteSize.Format(freeBefore)}, depois {ByteSize.Format(freeAfter)}");
        return result;
    }
}
