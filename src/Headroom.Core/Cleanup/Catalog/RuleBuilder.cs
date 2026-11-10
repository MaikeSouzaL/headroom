using Headroom.Core.Cleanup.Rules;

namespace Headroom.Core.Cleanup.Catalog;

/// <summary>Sintaxe fluente para declarar regras por caminho no catálogo embutido.</summary>
internal static class RuleBuilder
{
    public static PathRuleDefinition Def(string id, CleanupCategory category, string name, string description,
        RiskLevel risk = RiskLevel.Safe) => new()
    {
        Id = id,
        Category = category,
        Name = name,
        Description = description,
        Risk = risk,
    };

    public static PathRuleDefinition Contents(this PathRuleDefinition d, string path, int minAgeDays = 0, string pattern = "*",
        bool recursive = true, string[]? extensions = null, string[]? exclude = null, long minSize = 0)
    {
        d.Targets.Add(new PathTarget
        {
            Path = path,
            Mode = TargetMode.Contents,
            MinAgeDays = minAgeDays,
            Pattern = pattern,
            Recursive = recursive,
            Extensions = extensions?.ToList(),
            Exclude = exclude?.ToList(),
            MinSize = minSize,
        });
        return d;
    }

    public static PathRuleDefinition Self(this PathRuleDefinition d, string path, int minAgeDays = 0)
    {
        d.Targets.Add(new PathTarget { Path = path, Mode = TargetMode.Self, MinAgeDays = minAgeDays });
        return d;
    }

    public static PathRuleDefinition Children(this PathRuleDefinition d, string path, string pattern = "*", int minAgeDays = 0)
    {
        d.Targets.Add(new PathTarget { Path = path, Mode = TargetMode.Children, Pattern = pattern, MinAgeDays = minAgeDays });
        return d;
    }

    public static PathRuleDefinition Admin(this PathRuleDefinition d)
    {
        d.RequiresAdmin = true;
        return d;
    }

    public static PathRuleDefinition Off(this PathRuleDefinition d)
    {
        d.EnabledByDefault = false;
        return d;
    }

    public static PathRuleDefinition Unselected(this PathRuleDefinition d)
    {
        d.ItemsSelectedByDefault = false;
        return d;
    }

    public static PathRuleDefinition Close(this PathRuleDefinition d, params string[] processes)
    {
        d.ProcessesToClose.AddRange(processes);
        return d;
    }

    public static PathRuleDefinition CloseRequired(this PathRuleDefinition d, params string[] processes)
    {
        d.ProcessesToClose.AddRange(processes);
        d.RequiresProcessesClosed = true;
        return d;
    }

    public static PathRuleDefinition Services(this PathRuleDefinition d, params string[] services)
    {
        d.ServicesToStop.AddRange(services);
        return d;
    }

    public static PathRuleDefinition Detect(this PathRuleDefinition d, params string[] paths)
    {
        d.Detect.AddRange(paths);
        return d;
    }

    public static PathRuleDefinition Permanent(this PathRuleDefinition d)
    {
        d.AlwaysPermanent = true;
        return d;
    }

    public static PathRuleDefinition Privacy(this PathRuleDefinition d)
    {
        d.AllowBrowserPrivacyData = true;
        return d;
    }

    public static PathRule Build(this PathRuleDefinition d) => new(d);
}
