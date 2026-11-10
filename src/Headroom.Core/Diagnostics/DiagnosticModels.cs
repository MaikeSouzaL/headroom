namespace Headroom.Core.Diagnostics;

/// <summary>Gravidade de um item do diagnóstico.</summary>
public enum HealthStatus
{
    Ok,
    Info,
    Attention,
    Problem,
}

/// <summary>Um resultado do diagnóstico (só leitura — nada é alterado ao diagnosticar).</summary>
/// <param name="Category">Grupo exibido (Disco, Memória, Sistema, Segurança...).</param>
/// <param name="Title">Título curto.</param>
/// <param name="Status">Gravidade.</param>
/// <param name="Summary">Frase principal com o valor medido.</param>
/// <param name="Recommendation">O que fazer (quando houver).</param>
/// <param name="Details">Linhas adicionais (ex.: lista de dispositivos com problema).</param>
/// <param name="ActionId">Ação sugerida: id de um reparo ("repair.sfc") ou página ("page:Cleanup").</param>
/// <param name="ActionText">Texto do botão da ação sugerida.</param>
public sealed record DiagnosticFinding(
    string Category,
    string Title,
    HealthStatus Status,
    string Summary,
    string? Recommendation = null,
    IReadOnlyList<string>? Details = null,
    string? ActionId = null,
    string? ActionText = null);

/// <summary>Relatório completo, com nota de 0 a 100.</summary>
public sealed class DiagnosticReport
{
    public DiagnosticReport(IReadOnlyList<DiagnosticFinding> findings, TimeSpan elapsed)
    {
        Findings = findings;
        Elapsed = elapsed;
        Date = DateTime.Now;
    }

    public IReadOnlyList<DiagnosticFinding> Findings { get; }
    public TimeSpan Elapsed { get; }
    public DateTime Date { get; }

    public int Problems => Findings.Count(f => f.Status == HealthStatus.Problem);
    public int Attentions => Findings.Count(f => f.Status == HealthStatus.Attention);

    /// <summary>100 menos 15 por problema e 5 por atenção (mínimo 0).</summary>
    public int Score => Math.Max(0, 100 - Problems * 15 - Attentions * 5);

    public string ScoreText => Score switch
    {
        >= 90 => "Ótimo",
        >= 75 => "Bom",
        >= 50 => "Precisa de atenção",
        _ => "Crítico",
    };
}

public static class HealthStatusInfo
{
    public static string DisplayName(HealthStatus status) => status switch
    {
        HealthStatus.Ok => "OK",
        HealthStatus.Info => "Informação",
        HealthStatus.Attention => "Atenção",
        _ => "Problema",
    };
}
