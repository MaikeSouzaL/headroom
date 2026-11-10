namespace Headroom.Core.Settings;

/// <summary>Locais de dados do aplicativo (todos em %LOCALAPPDATA%\Headroom).</summary>
public static class AppPaths
{
    private static string? _dataDirectoryOverride;

    /// <summary>Permite redirecionar os dados (usado pelos testes).</summary>
    public static void OverrideDataDirectory(string? path) => _dataDirectoryOverride = path;

    public static string DataDirectory => _dataDirectoryOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Headroom");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public static string RegistryBackupsDirectory => Path.Combine(DataDirectory, "registro-backups");
    public static string UserRulesDirectory => Path.Combine(DataDirectory, "Rules");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string HistoryFile => Path.Combine(DataDirectory, "historico.json");

    /// <summary>Regras JSON distribuídas com o app (pasta Rules ao lado do executável).</summary>
    public static string BuiltInRulesDirectory => Path.Combine(AppContext.BaseDirectory, "Rules");

    /// <summary>Caminho do executável atual (funciona também em single-file).</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Headroom.exe");

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
