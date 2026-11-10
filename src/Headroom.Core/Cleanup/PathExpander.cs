using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Headroom.Core.Cleanup;

/// <summary>
/// Expande caminhos de regras: variáveis de ambiente (%LOCALAPPDATA%), variáveis próprias com vários
/// valores (%STEAMLIBS%, %DROPBOX%, %USERPROFILES%...) e curingas em qualquer segmento
/// ("User Data\*\Cache"). Pastas que são links/junctions nunca são usadas para casar curingas.
/// </summary>
public sealed partial class PathExpander
{
    private readonly Lazy<Dictionary<string, IReadOnlyList<string>>> _variables;

    public PathExpander()
    {
        _variables = new Lazy<Dictionary<string, IReadOnlyList<string>>>(BuildVariables);
    }

    /// <summary>Permite substituir/acrescentar variáveis (usado pelos testes).</summary>
    public void SetVariable(string name, params string[] values) =>
        _variables.Value[name.Trim('%').ToUpperInvariant()] = values;

    /// <summary>Expande um padrão em todos os caminhos existentes que ele representa.</summary>
    public IReadOnlyList<string> Expand(string pattern)
    {
        var results = new List<string>();
        foreach (var withVars in ExpandVariables(pattern))
        {
            string expanded = Environment.ExpandEnvironmentVariables(withVars);
            if (expanded.Contains('%') && VariableRegex().IsMatch(expanded))
            {
                continue; // variável não resolvida: ignora o padrão
            }

            foreach (var path in ExpandWildcards(expanded.Replace('/', '\\')))
            {
                if (!results.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(path);
                }
            }
        }

        return results;
    }

    /// <summary>Resolve uma variável de valor único (ou null).</summary>
    public string? Resolve(string variable)
    {
        string key = variable.Trim('%').ToUpperInvariant();
        if (_variables.Value.TryGetValue(key, out var values))
        {
            return values.FirstOrDefault();
        }

        string env = Environment.ExpandEnvironmentVariables("%" + key + "%");
        return env.StartsWith('%') ? null : env;
    }

    private IEnumerable<string> ExpandVariables(string pattern)
    {
        var match = VariableRegex().Matches(pattern)
            .FirstOrDefault(m => _variables.Value.ContainsKey(m.Groups[1].Value.ToUpperInvariant()));
        if (match is null)
        {
            yield return pattern;
            yield break;
        }

        foreach (var value in _variables.Value[match.Groups[1].Value.ToUpperInvariant()])
        {
            string replaced = pattern[..match.Index] + value.TrimEnd('\\') + pattern[(match.Index + match.Length)..];
            foreach (var further in ExpandVariables(replaced))
            {
                yield return further;
            }
        }
    }

    private static IEnumerable<string> ExpandWildcards(string path)
    {
        if (path.IndexOfAny(['*', '?']) < 0)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                yield return Path.TrimEndingDirectorySeparator(path);
            }

            yield break;
        }

        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            yield break;
        }

        var segments = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string> { root };
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            bool last = i == segments.Length - 1;
            var next = new List<string>();
            foreach (var basePath in current)
            {
                if (segment.IndexOfAny(['*', '?']) < 0)
                {
                    string candidate = Path.Combine(basePath, segment);
                    if (last ? File.Exists(candidate) || Directory.Exists(candidate) : Directory.Exists(candidate))
                    {
                        next.Add(candidate);
                    }

                    continue;
                }

                foreach (var match in MatchChildren(basePath, segment, includeFiles: last))
                {
                    next.Add(match);
                }
            }

            current = next;
            if (current.Count == 0)
            {
                yield break;
            }
        }

        foreach (var p in current)
        {
            yield return p;
        }
    }

    private static IEnumerable<string> MatchChildren(string directory, string pattern, bool includeFiles)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // nunca casar curinga com link/junction
            RecurseSubdirectories = false,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        IEnumerable<string> entries;
        try
        {
            entries = includeFiles
                ? Directory.EnumerateFileSystemEntries(directory, pattern, options).ToList()
                : Directory.EnumerateDirectories(directory, pattern, options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            yield return entry;
        }
    }

    /// <summary>Testa um nome contra um curinga simples (* e ?), sem diferenciar maiúsculas.</summary>
    public static bool MatchesWildcard(string name, string pattern) =>
        FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    private static Dictionary<string, IReadOnlyList<string>> BuildVariables()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var vars = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["DESKTOP"] = [Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)],
            ["DOCUMENTS"] = [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)],
            ["DOWNLOADS"] = [KnownDownloads(profile)],
            ["PICTURES"] = [Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)],
            ["VIDEOS"] = [Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)],
            ["MUSIC"] = [Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)],
            ["STARTMENU"] = [Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)],
            ["LOCALLOW"] = [Path.Combine(profile, "AppData", "LocalLow")],
            ["WINDIR"] = [Environment.GetFolderPath(Environment.SpecialFolder.Windows)],
        };

        string? steam = SteamPath();
        if (steam is not null)
        {
            vars["STEAM"] = [steam];
            vars["STEAMLIBS"] = SteamLibraries(steam);
        }

        var dropbox = DropboxFolders(local);
        if (dropbox.Count > 0)
        {
            vars["DROPBOX"] = dropbox;
        }

        string sdk = Environment.GetEnvironmentVariable("ANDROID_HOME") ??
                     Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ??
                     Path.Combine(local, "Android", "Sdk");
        vars["ANDROIDSDK"] = [sdk];
        vars["USERPROFILES"] = OtherUserProfiles(profile);
        return vars;
    }

    private static string KnownDownloads(string profile)
    {
        try
        {
            // A pasta Downloads pode ter sido movida; o caminho real fica no registro (GUID da pasta conhecida).
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
            if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string path && Directory.Exists(path))
            {
                return path;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
        }

        return Path.Combine(profile, "Downloads");
    }

    private static string? SteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
            {
                return Path.GetFullPath(path.Replace('/', '\\'));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
        }

        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>Lê steamapps\libraryfolders.vdf para achar todas as bibliotecas de jogos.</summary>
    internal static IReadOnlyList<string> SteamLibraries(string steam)
    {
        var libraries = new List<string> { steam };
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                foreach (Match m in SteamPathRegex().Matches(File.ReadAllText(vdf)))
                {
                    string lib = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(lib) && !libraries.Contains(lib, StringComparer.OrdinalIgnoreCase))
                    {
                        libraries.Add(lib);
                    }
                }
            }
        }
        catch (IOException)
        {
        }

        return libraries;
    }

    private static IReadOnlyList<string> DropboxFolders(string localAppData)
    {
        var result = new List<string>();
        foreach (var info in new[] { Path.Combine(localAppData, "Dropbox", "info.json"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json") })
        {
            try
            {
                if (!File.Exists(info))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(info));
                foreach (var account in doc.RootElement.EnumerateObject())
                {
                    if (account.Value.ValueKind == JsonValueKind.Object &&
                        account.Value.TryGetProperty("path", out var p) && p.GetString() is { } path && Directory.Exists(path))
                    {
                        result.Add(path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private static IReadOnlyList<string> OtherUserProfiles(string currentProfile)
    {
        var result = new List<string>();
        string? users = Path.GetDirectoryName(currentProfile);
        if (users is null)
        {
            return result;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(users))
            {
                string name = Path.GetFileName(dir);
                if (name is "Public" or "Default" or "Default User" or "All Users" or "defaultuser0" ||
                    dir.Equals(currentProfile, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                result.Add(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    [GeneratedRegex("%([A-Za-z0-9_()]+)%")]
    private static partial Regex VariableRegex();

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathRegex();
}
