using System.Text.Json;
using System.Text.Json.Serialization;

namespace Headroom.Core.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum ScheduleFrequency
{
    Daily,
    Weekly,
    Monthly,
}

/// <summary>Configurações persistidas do usuário.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Idioma da interface. Atualmente só pt-BR é distribuído.</summary>
    public string Language { get; set; } = "pt-BR";

    /// <summary>Tamanho mínimo padrão no filtro de "Maiores arquivos".</summary>
    public long LargeFileMinimumBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Quantos maiores arquivos o scanner guarda.</summary>
    public int TopFilesCount { get; set; } = 500;

    /// <summary>Pastas ignoradas pelo scanner.</summary>
    public List<string> ExcludedPaths { get; set; } = new();

    public long DuplicateMinimumBytes { get; set; } = 1024 * 1024;

    /// <summary>Mover para a Lixeira por padrão (exclusão permanente exige confirmação explícita).</summary>
    public bool UseRecycleBin { get; set; } = true;

    /// <summary>Projetos sem modificação há mais de X dias são considerados parados.</summary>
    public int StaleProjectDays { get; set; } = 90;

    /// <summary>Pastas onde procurar projetos de desenvolvimento (além da pasta do usuário).</summary>
    public List<string> DevProjectRoots { get; set; } = new();

    public int OldDownloadsDays { get; set; } = 90;

    /// <summary>"Arquivos grandes e antigos": tamanho mínimo e idade em meses.</summary>
    public long OldLargeFileMinimumBytes { get; set; } = 500L * 1024 * 1024;
    public int OldLargeFileMonths { get; set; } = 12;

    public int OldScreenshotsDays { get; set; } = 180;

    /// <summary>Pastas escolhidas para compressão NTFS (regra avançada).</summary>
    public List<string> CompressionFolders { get; set; } = new();

    public bool CreateRestorePointBeforeAdvanced { get; set; } = true;

    public bool FreeSpaceAlertEnabled { get; set; } = true;
    public double FreeSpaceAlertPercent { get; set; } = 10;

    /// <summary>Perfil de limpeza selecionado por último.</summary>
    public string SelectedProfileId { get; set; } = "rapida";

    /// <summary>Regras marcadas no perfil "Personalizada".</summary>
    public List<string> CustomProfileRuleIds { get; set; } = new();

    public bool ScheduleEnabled { get; set; }
    public ScheduleFrequency ScheduleFrequency { get; set; } = ScheduleFrequency.Weekly;
    public string ScheduleTime { get; set; } = "10:00";
    public string ScheduleProfileId { get; set; } = "rapida";

    /// <summary>Guarda cada análise para reabrir depois sem escanear de novo.</summary>
    public bool SaveAnalyses { get; set; } = true;

    /// <summary>Ao abrir o app, reabre as últimas análises salvas.</summary>
    public bool OpenLastAnalysisOnStart { get; set; } = true;

    /// <summary>Discos/pastas da última análise (reabertos ao iniciar).</summary>
    public List<string> LastAnalysisRoots { get; set; } = new();

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

/// <summary>Leitura e gravação das configurações em JSON.</summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions);
                if (settings is not null)
                {
                    Normalize(settings);
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Arquivo corrompido ou inacessível: volta aos padrões.
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static void Normalize(AppSettings s)
    {
        s.TopFilesCount = Math.Clamp(s.TopFilesCount, 10, 100_000);
        s.StaleProjectDays = Math.Clamp(s.StaleProjectDays, 1, 3650);
        s.OldDownloadsDays = Math.Clamp(s.OldDownloadsDays, 1, 3650);
        s.OldLargeFileMonths = Math.Clamp(s.OldLargeFileMonths, 1, 240);
        s.OldScreenshotsDays = Math.Clamp(s.OldScreenshotsDays, 1, 3650);
        s.FreeSpaceAlertPercent = Math.Clamp(s.FreeSpaceAlertPercent, 1, 50);
        s.ExcludedPaths ??= new();
        s.DevProjectRoots ??= new();
        s.CompressionFolders ??= new();
        s.CustomProfileRuleIds ??= new();
        s.LastAnalysisRoots ??= new();
        if (!TimeOnly.TryParse(s.ScheduleTime, out _))
        {
            s.ScheduleTime = "10:00";
        }
    }
}
