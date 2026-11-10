using System.Text.Json.Serialization;
using Headroom.Core.Cleanup.Deletion;

namespace Headroom.Core.Cleanup;

/// <summary>Nível de risco exibido na interface.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RiskLevel
{
    /// <summary>Seguro: caches e temporários que o Windows/apps recriam sozinhos.</summary>
    Safe,

    /// <summary>Revisar: pode remover algo que o usuário queira; os itens são mostrados antes.</summary>
    Review,

    /// <summary>Avançado: mexe em recursos do sistema (DISM, hibernação, pontos de restauração...).</summary>
    Advanced,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CleanupCategory
{
    System,
    Browsers,
    Applications,
    Games,
    Developer,
    UserFiles,
    Privacy,
    Advanced,
}

public static class CleanupCategoryInfo
{
    public static readonly CleanupCategory[] Ordered =
    [
        CleanupCategory.System, CleanupCategory.Browsers, CleanupCategory.Applications, CleanupCategory.Games,
        CleanupCategory.Developer, CleanupCategory.UserFiles, CleanupCategory.Privacy, CleanupCategory.Advanced,
    ];

    public static string DisplayName(CleanupCategory category) => category switch
    {
        CleanupCategory.System => "Sistema Windows",
        CleanupCategory.Browsers => "Navegadores",
        CleanupCategory.Applications => "Aplicativos",
        CleanupCategory.Games => "Jogos",
        CleanupCategory.Developer => "Desenvolvedor",
        CleanupCategory.UserFiles => "Arquivos do usuário",
        CleanupCategory.Privacy => "Privacidade",
        CleanupCategory.Advanced => "Avançado",
        _ => category.ToString(),
    };

    public static string RiskName(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => "Seguro",
        RiskLevel.Review => "Revisar",
        RiskLevel.Advanced => "Avançado",
        _ => risk.ToString(),
    };
}

/// <summary>Um item encontrado na análise (arquivo, pasta ou ação), selecionável na prévia.</summary>
public sealed class CleanupItem
{
    public CleanupItem(string path, long size, bool isDirectory, DateTime lastModified, int fileCount = 1)
    {
        Path = path;
        Size = size;
        IsDirectory = isDirectory;
        LastModified = lastModified;
        FileCount = fileCount;
    }

    public string Path { get; }
    public long Size { get; }
    public bool IsDirectory { get; }
    public DateTime LastModified { get; }
    public int FileCount { get; }

    /// <summary>Nome amigável (ex.: "meta-llama/Llama-3" para uma pasta de modelo).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Por que o item foi listado (ex.: motivo de uma pasta ser considerada órfã).</summary>
    public string? Reason { get; init; }

    /// <summary>Dado extra usado pela regra na limpeza (ex.: nome do modelo para "ollama rm").</summary>
    public string? Tag { get; init; }

    public bool IsSelected { get; set; } = true;

    public string Name => DisplayName ?? System.IO.Path.GetFileName(Path.TrimEnd('\\'));
}

/// <summary>Resultado de "Analisar" de uma regra.</summary>
public sealed class CleanupAnalysis
{
    public CleanupAnalysis(ICleanupRule rule, IReadOnlyList<CleanupItem> items, long? estimatedBytes = null, string? note = null)
    {
        Rule = rule;
        Items = items;
        EstimatedBytes = estimatedBytes;
        Note = note;
    }

    public ICleanupRule Rule { get; }
    public IReadOnlyList<CleanupItem> Items { get; }

    /// <summary>Estimativa para regras sem itens de arquivo (comandos). Null quando há itens.</summary>
    public long? EstimatedBytes { get; }

    /// <summary>Observação mostrada ao usuário (ex.: "Requer administrador", "Docker não está rodando").</summary>
    public string? Note { get; init; }

    /// <summary>A regra é só uma ação (comando), sem lista de arquivos.</summary>
    public bool IsAction { get; init; }

    /// <summary>A análise falhou ou a regra não pode rodar agora.</summary>
    public bool IsUnavailable { get; init; }

    public long TotalBytes => EstimatedBytes ?? Items.Sum(i => i.Size);
    public long SelectedBytes => IsAction ? TotalBytes : Items.Where(i => i.IsSelected).Sum(i => i.Size);
    public int SelectedCount => Items.Count(i => i.IsSelected);
    public bool HasWork => !IsUnavailable && (IsAction || Items.Any(i => i.IsSelected));

    public static CleanupAnalysis Unavailable(ICleanupRule rule, string note) =>
        new(rule, Array.Empty<CleanupItem>(), 0, note) { IsUnavailable = true };

    public static CleanupAnalysis Action(ICleanupRule rule, long? estimatedBytes, string note) =>
        new(rule, Array.Empty<CleanupItem>(), estimatedBytes ?? 0, note) { IsAction = true };
}

/// <summary>Resultado da limpeza de uma regra.</summary>
public sealed class CleanupRunResult
{
    public CleanupRunResult(ICleanupRule rule)
    {
        Rule = rule;
    }

    public ICleanupRule Rule { get; }
    public long BytesFreed { get; set; }
    public DeletionResult? Deletion { get; set; }
    public bool Success { get; set; } = true;
    public string? Message { get; set; }
    public string? Output { get; set; }
    public bool MovedToRecycleBin { get; set; }
    public int Skipped => Deletion?.TotalSkipped ?? 0;
    public int Errors => (Deletion?.Errors ?? 0) + (Success ? 0 : 1);
}

public sealed record CleanupProgress(string RuleName, string Detail, double Fraction);
