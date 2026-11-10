using Headroom.Core.Cleanup.Declarative;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Settings;

namespace Headroom.Core.Cleanup.Catalog;

/// <summary>Todas as regras disponíveis: embutidas (C#) + declarativas (JSON).</summary>
public sealed class RuleCatalog
{
    private RuleCatalog(IReadOnlyList<ICleanupRule> rules, IReadOnlyList<RuleLoadError> errors)
    {
        Rules = rules;
        LoadErrors = errors;
    }

    public IReadOnlyList<ICleanupRule> Rules { get; }

    /// <summary>Erros ao ler regras JSON (mostrados nas Configurações).</summary>
    public IReadOnlyList<RuleLoadError> LoadErrors { get; }

    public static IReadOnlyList<ICleanupRule> BuiltIn(AppSettings settings) =>
        SystemCatalog.Create(settings)
            .Concat(BrowserCatalog.Create())
            .Concat(AppCatalog.Create())
            .Concat(DevCatalog.Create())
            .Concat(UserAndPrivacyCatalog.CreateUserFiles(settings))
            .Concat(UserAndPrivacyCatalog.CreatePrivacy())
            .ToList();

    /// <summary>
    /// Monta o catálogo. Regras JSON com o mesmo id de uma embutida a substituem.
    /// Por segurança, quando o app roda como administrador, comandos de regras JSON da pasta do
    /// usuário (gravável sem privilégio) são ignorados.
    /// </summary>
    public static RuleCatalog Create(AppSettings settings, bool isAdministrator, IEnumerable<string>? jsonDirectories = null)
    {
        var builtIn = BuiltIn(settings);
        var directories = (jsonDirectories ?? new[] { AppPaths.BuiltInRulesDirectory, AppPaths.UserRulesDirectory }).ToList();
        var (jsonRules, errors) = JsonRuleLoader.LoadDirectories(directories);

        var byId = new Dictionary<string, ICleanupRule>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var rule in builtIn)
        {
            if (byId.TryAdd(rule.Id, rule))
            {
                order.Add(rule.Id);
            }
        }

        foreach (var rule in jsonRules)
        {
            var effective = rule;
            if (isAdministrator && rule is PathRule pathRule && pathRule.Definition.Command is not null &&
                rule.Source.StartsWith(AppPaths.UserRulesDirectory, StringComparison.OrdinalIgnoreCase))
            {
                pathRule.Definition.Command = null;
                errors.Add(new RuleLoadError(rule.Source,
                    $"Regra \"{rule.Id}\": o comando foi ignorado porque o Headroom está rodando como administrador e a regra vem da pasta do usuário."));
                effective = new PathRule(pathRule.Definition, rule.Source);
            }

            if (!byId.ContainsKey(effective.Id))
            {
                order.Add(effective.Id);
            }

            byId[effective.Id] = effective;
        }

        return new RuleCatalog(order.Select(id => byId[id]).ToList(), errors);
    }
}
