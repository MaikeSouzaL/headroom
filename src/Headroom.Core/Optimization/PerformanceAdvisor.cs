using Headroom.Core.Formatting;

namespace Headroom.Core.Optimization;

public enum AdviceLevel
{
    Ok,
    Info,
    Warning,
    Critical,
}

/// <summary>Recomendação com uma ação sugerida (ActionId interpretado pela interface).</summary>
/// <remarks>
/// ActionIds: "memory.free", "tab.processes", "tab.startup", "tab.tweaks", "tab.energy", "page:Cleanup", "page:Repairs".
/// </remarks>
public sealed record PerformanceAdvice(AdviceLevel Level, string Title, string Detail, string? ActionId = null, string? ActionText = null);

/// <summary>Estado atual usado para gerar recomendações.</summary>
public sealed record PerformanceSnapshot(
    MemoryDetails Memory,
    double CpuAverage,
    IReadOnlyList<ProcessSample> Processes,
    TimeSpan Uptime,
    int? StartupEnabledCount = null,
    string? PowerPlanName = null,
    int TweaksAvailable = 0);

/// <summary>Transforma as medições em recomendações práticas, das mais graves para as mais leves.</summary>
public static class PerformanceAdvisor
{
    public static IReadOnlyList<PerformanceAdvice> Advise(PerformanceSnapshot s)
    {
        var advice = new List<PerformanceAdvice>();

        // Candidatos a finalizar/baixar prioridade: nunca processos do sistema nem o próprio Headroom.
        var candidates = s.Processes.Where(p => !p.IsProtected).ToList();

        // Para dizer "quem mais usa", o próprio Headroom entra (um scan grande pesa de verdade).
        var listing = s.Processes.Where(p => !p.IsProtected || p.Id == Environment.ProcessId).ToList();

        foreach (var hung in candidates.Where(p => p.NotResponding).Take(3))
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Critical, $"{hung.DisplayName} não está respondendo",
                "O programa travou. Espere um pouco ou finalize-o (trabalho não salvo nele será perdido).",
                "tab.processes", "Ver e finalizar"));
        }

        double ram = s.Memory.UsedFraction;
        if (ram >= 0.80)
        {
            var top = listing.OrderByDescending(p => p.WorkingSet).Take(3)
                .Select(p => $"{p.DisplayName} ({ByteSize.Format(p.WorkingSet)})");
            advice.Add(new PerformanceAdvice(ram >= 0.90 ? AdviceLevel.Critical : AdviceLevel.Warning,
                $"Memória {(ram >= 0.90 ? "quase cheia" : "alta")}: {ByteSize.FormatPercent(ram)} em uso",
                $"Os que mais usam: {string.Join(", ", top)}. Feche o que não estiver usando ou libere memória.",
                "memory.free", "Liberar memória"));
        }

        if (s.Memory.CommitFraction >= 0.90)
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Warning, "Memória virtual perto do limite",
                $"{ByteSize.Format(s.Memory.CommitTotal)} de {ByteSize.Format(s.Memory.CommitLimit)} comprometidos. " +
                "Programas podem fechar sozinhos; feche alguns ou deixe o Windows gerenciar o arquivo de paginação.",
                "tab.processes", "Ver processos"));
        }

        if (s.CpuAverage >= 0.85)
        {
            var top = listing.OrderByDescending(p => p.CpuAverage).Take(3)
                .Select(p => $"{p.DisplayName} ({ByteSize.FormatPercent(p.CpuAverage)})");
            double system = s.Processes.Where(p => p.IsProtected && p.Id != Environment.ProcessId).Sum(p => p.CpuAverage);
            advice.Add(new PerformanceAdvice(AdviceLevel.Critical, $"Processador sobrecarregado ({ByteSize.FormatPercent(s.CpuAverage)})",
                $"Quem mais usa: {string.Join(", ", top)}." +
                (system >= 0.2 ? $" Processos do Windows (antivírus, indexação, serviços) somam {ByteSize.FormatPercent(system)}." : string.Empty),
                "tab.processes", "Ver processos"));
        }

        var heavy = candidates.Where(p => p.IsHeavy && !p.NotResponding)
            .OrderByDescending(p => p.CpuAverage).ThenByDescending(p => p.WorkingSet).Take(4).ToList();
        if (heavy.Count > 0)
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Warning,
                heavy.Count == 1 ? $"{heavy[0].DisplayName} está pesando" : $"{heavy.Count} programas estão pesando",
                string.Join(" · ", heavy.Select(Describe)) +
                ". Finalize, ou baixe a prioridade para que não atrapalhem o que você está usando.",
                "tab.processes", "Ver processos"));
        }

        if (s.StartupEnabledCount is int startup && startup >= 8)
        {
            advice.Add(new PerformanceAdvice(startup >= 15 ? AdviceLevel.Warning : AdviceLevel.Info,
                $"{startup} programas iniciam com o Windows",
                "Cada um deixa a inicialização mais lenta e fica ocupando memória. Desative os que não usa sempre (é reversível).",
                "tab.startup", "Revisar inicialização"));
        }

        if (s.PowerPlanName is { } plan &&
            (plan.Contains("Economia", StringComparison.OrdinalIgnoreCase) || plan.Contains("saver", StringComparison.OrdinalIgnoreCase)))
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Warning, "Plano de energia em Economia",
                "O processador fica limitado para economizar energia. Em PCs de mesa, use Equilibrado ou Alto desempenho.",
                "tab.energy", "Trocar plano"));
        }

        if (s.Uptime >= TimeSpan.FromDays(7))
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Info, $"Ligado há {(int)s.Uptime.TotalDays} dias sem reiniciar",
                "Reiniciar de vez em quando libera memória presa por programas e aplica atualizações pendentes."));
        }

        if (s.TweaksAvailable > 0)
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Info, $"{s.TweaksAvailable} ajuste(s) de desempenho disponíveis",
                "Efeitos visuais, apps em segundo plano, sugestões do Windows e jogos. Todos reversíveis.",
                "tab.tweaks", "Ver ajustes"));
        }

        advice.Add(new PerformanceAdvice(AdviceLevel.Info, "Caches e arquivos temporários",
            "A Limpeza remove caches do Windows, navegadores, apps e ferramentas de desenvolvimento, com prévia antes de apagar.",
            "page:Cleanup", "Abrir Limpeza"));

        var ordered = advice.OrderByDescending(a => a.Level).ToList();
        if (!advice.Any(a => a.Level is AdviceLevel.Warning or AdviceLevel.Critical))
        {
            ordered.Insert(0, new PerformanceAdvice(AdviceLevel.Ok, "Nada pesando agora",
                $"Processador em {ByteSize.FormatPercent(s.CpuAverage)} e memória em {ByteSize.FormatPercent(ram)}."));
        }

        return ordered;
    }

    /// <summary>"Chrome: 34% da CPU, 2,1 GB".</summary>
    public static string Describe(ProcessSample p)
    {
        var parts = new List<string>();
        if (p.Issues.Contains(ProcessIssue.HighCpu))
        {
            parts.Add($"{ByteSize.FormatPercent(p.CpuAverage)} da CPU");
        }

        if (p.Issues.Contains(ProcessIssue.HighMemory))
        {
            parts.Add(ByteSize.Format(p.WorkingSet) + " de RAM");
        }

        if (p.Issues.Contains(ProcessIssue.HighDisk))
        {
            parts.Add(ByteSize.Format((long)p.DiskBytesPerSecond) + "/s de disco");
        }

        if (p.Issues.Contains(ProcessIssue.NotResponding))
        {
            parts.Add("não responde");
        }

        return parts.Count == 0 ? p.DisplayName : $"{p.DisplayName}: {string.Join(", ", parts)}";
    }
}
