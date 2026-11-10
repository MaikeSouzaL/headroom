namespace Headroom.Core.Cleanup;

/// <summary>Perfil de limpeza: quais regras vêm marcadas.</summary>
public sealed class CleanupProfile
{
    private readonly Func<ICleanupRule, bool> _selector;

    public CleanupProfile(string id, string name, string description, Func<ICleanupRule, bool> selector)
    {
        Id = id;
        Name = name;
        Description = description;
        _selector = selector;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }

    public bool Includes(ICleanupRule rule) => !rule.IsInformational && _selector(rule);

    public override string ToString() => Name;
}

/// <summary>Perfis prontos: Rápida, Completa, Dev, Gamer e Personalizada (escolhas salvas).</summary>
public static class CleanupProfiles
{
    public const string Quick = "rapida";
    public const string Full = "completa";
    public const string Developer = "dev";
    public const string Gamer = "gamer";
    public const string Custom = "personalizada";

    private static bool SafeDefault(ICleanupRule r) => r.Risk == RiskLevel.Safe && r.EnabledByDefault;

    public static IReadOnlyList<CleanupProfile> All(IReadOnlyCollection<string> customRuleIds) =>
    [
        new(Quick, "Rápida", "Só itens seguros: temporários, caches e Lixeira.",
            r => SafeDefault(r) && r.Category is not CleanupCategory.Privacy),
        new(Full, "Completa", "Itens seguros de todas as categorias, mais revisões de sistema e aplicativos (sem ações avançadas).",
            r => (SafeDefault(r) || (r.Risk == RiskLevel.Review && r.EnabledByDefault)) &&
                 r.Category is not CleanupCategory.Advanced and not CleanupCategory.UserFiles),
        new(Developer, "Dev", "Sistema seguro + caches de desenvolvimento, IDEs e projetos parados.",
            r => (r.Category == CleanupCategory.System && SafeDefault(r)) ||
                 (r.Category == CleanupCategory.Developer && r.Risk != RiskLevel.Advanced &&
                  (r.EnabledByDefault || r.Id is "dev.staleprojects" or "dev.nuget" or "dev.docker.builder"))),
        new(Gamer, "Gamer", "Sistema seguro + caches de shaders, launchers de jogos e navegadores.",
            r => (r.Category is CleanupCategory.System or CleanupCategory.Games or CleanupCategory.Browsers && SafeDefault(r)) ||
                 r.Id.StartsWith("sys.shader", StringComparison.Ordinal)),
        new(Custom, "Personalizada", "As regras que você marcou da última vez.",
            r => customRuleIds.Contains(r.Id)),
    ];

    public static CleanupProfile Find(string id, IReadOnlyCollection<string> customRuleIds) =>
        All(customRuleIds).FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All(customRuleIds)[0];
}
