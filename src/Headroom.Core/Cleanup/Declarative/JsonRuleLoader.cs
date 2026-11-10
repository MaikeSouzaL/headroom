using System.Text.Json;
using System.Text.Json.Serialization;
using Headroom.Core.Cleanup.Rules;

namespace Headroom.Core.Cleanup.Declarative;

/// <summary>Problema encontrado ao ler um arquivo de regras.</summary>
public sealed record RuleLoadError(string File, string Message);

/// <summary>
/// Carrega regras declarativas em JSON (pasta Rules ao lado do executável e
/// %LOCALAPPDATA%\Headroom\Rules), no estilo do CleanerML do BleachBit. Permite criar limpezas
/// novas sem recompilar. Cada arquivo pode ter uma regra ou uma lista de regras.
/// </summary>
/// <example>
/// {
///   "id": "meuapp.cache",
///   "name": "Meu App: cache",
///   "description": "Cache do Meu App.",
///   "category": "Applications",
///   "risk": "Safe",
///   "processesToClose": ["meuapp.exe"],
///   "detect": ["%LOCALAPPDATA%\\MeuApp"],
///   "targets": [ { "path": "%LOCALAPPDATA%\\MeuApp\\Cache", "minAgeDays": 0, "extensions": [".tmp"] } ]
/// }
/// </example>
public static class JsonRuleLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static (List<ICleanupRule> Rules, List<RuleLoadError> Errors) LoadDirectories(IEnumerable<string> directories)
    {
        var rules = new List<ICleanupRule>();
        var errors = new List<RuleLoadError>();
        foreach (var directory in directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var (loaded, fileErrors) = LoadFile(file);
                rules.AddRange(loaded);
                errors.AddRange(fileErrors);
            }
        }

        return (rules, errors);
    }

    public static (List<ICleanupRule> Rules, List<RuleLoadError> Errors) LoadFile(string file)
    {
        try
        {
            return Parse(File.ReadAllText(file), file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new List<ICleanupRule>(), new List<RuleLoadError> { new(file, "Não foi possível ler o arquivo: " + ex.Message) });
        }
    }

    public static (List<ICleanupRule> Rules, List<RuleLoadError> Errors) Parse(string json, string source)
    {
        var rules = new List<ICleanupRule>();
        var errors = new List<RuleLoadError>();
        List<PathRuleDefinition>? definitions;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            definitions = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.Deserialize<List<PathRuleDefinition>>(Options)
                : doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("rules", out var list)
                    ? list.Deserialize<List<PathRuleDefinition>>(Options)
                    : new List<PathRuleDefinition> { doc.RootElement.Deserialize<PathRuleDefinition>(Options)! };
        }
        catch (JsonException ex)
        {
            errors.Add(new RuleLoadError(source, $"JSON inválido (linha {ex.LineNumber + 1}): {ex.Message}"));
            return (rules, errors);
        }

        foreach (var definition in definitions ?? new List<PathRuleDefinition>())
        {
            string? problem = Validate(definition);
            if (problem is not null)
            {
                errors.Add(new RuleLoadError(source, $"Regra \"{definition.Id}\": {problem}"));
                continue;
            }

            rules.Add(new PathRule(definition, source));
        }

        return (rules, errors);
    }

    /// <summary>Validação: id, nome e alvos obrigatórios; caminhos precisam ser absolutos (após variáveis).</summary>
    public static string? Validate(PathRuleDefinition d)
    {
        if (string.IsNullOrWhiteSpace(d.Id))
        {
            return "o campo \"id\" é obrigatório.";
        }

        if (string.IsNullOrWhiteSpace(d.Name))
        {
            return "o campo \"name\" é obrigatório.";
        }

        if (d.Targets.Count == 0)
        {
            return "informe pelo menos um item em \"targets\".";
        }

        foreach (var target in d.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.Path))
            {
                return "todo alvo precisa de \"path\".";
            }

            string expanded = Environment.ExpandEnvironmentVariables(target.Path);
            bool usesCustomVariable = target.Path.StartsWith('%');
            if (!usesCustomVariable && !Path.IsPathFullyQualified(expanded))
            {
                return $"o caminho \"{target.Path}\" precisa ser absoluto ou começar com uma variável (%LOCALAPPDATA%...).";
            }

            if (target.Path.Contains("..", StringComparison.Ordinal))
            {
                return "caminhos com \"..\" não são permitidos.";
            }

            if (target.MinAgeDays < 0 || target.MinSize < 0)
            {
                return "minAgeDays/minSize não podem ser negativos.";
            }
        }

        return null;
    }
}
