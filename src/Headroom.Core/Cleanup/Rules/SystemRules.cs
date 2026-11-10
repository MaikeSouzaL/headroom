using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Headroom.Core.Cleanup.Safety;
using Headroom.Core.Formatting;
using Headroom.Core.SystemTools;
using Microsoft.Win32;

namespace Headroom.Core.Cleanup.Rules;

/// <summary>Esvazia a Lixeira de todos os discos (SHEmptyRecycleBin). É sempre a primeira a rodar.</summary>
public sealed class RecycleBinRule : CleanupRuleBase
{
    public const string RuleId = "sys.recyclebin";

    public RecycleBinRule()
        : base(RuleId, CleanupCategory.System, "Lixeira (todos os discos)",
            "Esvazia a Lixeira de todos os discos. Os itens da Lixeira são excluídos definitivamente.", RiskLevel.Safe)
    {
        AlwaysPermanent = true;
    }

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        var items = new List<CleanupItem>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
            if (SHQueryRecycleBinW(drive.RootDirectory.FullName, ref info) == 0 && info.i64NumItems > 0)
            {
                items.Add(new CleanupItem(Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin"), info.i64Size, true,
                    DateTime.Now, (int)Math.Min(int.MaxValue, info.i64NumItems))
                {
                    DisplayName = $"Lixeira de {drive.Name.TrimEnd('\\')} ({ByteSize.FormatCount(info.i64NumItems)} itens)",
                });
            }
        }

        var analysis = new CleanupAnalysis(this, items, null, items.Count == 0 ? "A Lixeira já está vazia." : null);
        return Task.FromResult(analysis);
    }

    public override Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new CleanupRunResult(this);
        execution.Log.Section($"{Name} [{Id}]");
        foreach (var item in analysis.Items.Where(i => i.IsSelected))
        {
            string root = Path.GetPathRoot(item.Path)!;
            progress?.Report(new CleanupProgress(Name, item.Name, 0.5));
            int hr = SHEmptyRecycleBinW(IntPtr.Zero, root, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
            if (hr == 0 || hr == unchecked((int)0x8000FFFF))
            {
                // 0x8000FFFF (E_UNEXPECTED) é devolvido quando a Lixeira já está vazia.
                result.BytesFreed += item.Size;
                execution.Log.Item("LIXEIRA ESVAZIADA", item.Size, item.Path);
            }
            else
            {
                result.Success = false;
                result.Message = $"Não foi possível esvaziar a Lixeira de {root} (0x{hr:X8}).";
                execution.Log.Item("ERRO", item.Size, item.Path, result.Message);
            }
        }

        return Task.FromResult(result);
    }

    private const uint SherbNoConfirmation = 0x1;
    private const uint SherbNoProgressUi = 0x2;
    private const uint SherbNoSound = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string pszRootPath, uint dwFlags);
}

/// <summary>
/// Regra por caminho para pastas protegidas pelo TrustedInstaller (Windows.old, $Windows.~BT...).
/// Antes de apagar, toma posse para o grupo Administradores (icacls, independe do idioma).
/// </summary>
public sealed class TakeOwnershipPathRule : PathRule
{
    public TakeOwnershipPathRule(PathRuleDefinition definition) : base(definition)
    {
    }

    public override async Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        if (!execution.IsAdministrator)
        {
            return new CleanupRunResult(this) { Success = false, Message = "Requer administrador." };
        }

        foreach (var item in analysis.Items.Where(i => i.IsSelected))
        {
            // Só mexe em permissões de itens que a lista negra permite apagar.
            if (DeletionGuard.Default.Check(item.Path).IsProtected)
            {
                continue;
            }

            progress?.Report(new CleanupProgress(Name, "Ajustando permissões: " + item.Path, 0));
            string icacls = CommandRunner.SystemTool("icacls.exe");
            await CommandRunner.RunAsync(icacls, $"\"{item.Path}\" /setowner *S-1-5-32-544 /T /C /Q", TimeSpan.FromMinutes(60),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await CommandRunner.RunAsync(icacls, $"\"{item.Path}\" /grant *S-1-5-32-544:F /T /C /Q", TimeSpan.FromMinutes(60),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            execution.Log.Info($"Permissões ajustadas para Administradores: {item.Path}");
        }

        return await base.CleanAsync(analysis, execution, progress, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Apaga pontos de restauração/Shadow Copies antigos, mantendo o mais recente de cada disco.</summary>
public sealed class ShadowCopiesRule : CleanupRuleBase
{
    public ShadowCopiesRule()
        : base("sys.shadowcopies", CleanupCategory.Advanced, "Pontos de restauração antigos (Shadow Copies)",
            "Apaga todos os pontos de restauração e cópias de sombra, exceto o mais recente de cada disco (vssadmin).",
            RiskLevel.Advanced)
    {
        RequiresAdmin = true;
        AlwaysPermanent = true;
        EnabledByDefault = false;
    }

    public override async Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        if (!context.IsAdministrator)
        {
            return CleanupAnalysis.Unavailable(this, "Requer administrador.");
        }

        const string script =
            "$s = Get-CimInstance Win32_ShadowCopy -ErrorAction SilentlyContinue | Select-Object ID, VolumeName, @{n='Date';e={$_.InstallDate.ToString('o')}}; " +
            "$st = Get-CimInstance Win32_ShadowStorage -ErrorAction SilentlyContinue | Select-Object @{n='Volume';e={$_.Volume.DeviceID}}, UsedSpace; " +
            "@{ Shadows = @($s); Storage = @($st) } | ConvertTo-Json -Depth 4 -Compress";
        var run = await CommandRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(2), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var items = new List<CleanupItem>();
        try
        {
            using var doc = JsonDocument.Parse(run.Output.Trim());
            var storage = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in JsonHelpers.Array(doc.RootElement, "Storage"))
            {
                storage[JsonHelpers.Str(s, "Volume") ?? string.Empty] = JsonHelpers.Long(s, "UsedSpace");
            }

            var shadows = JsonHelpers.Array(doc.RootElement, "Shadows")
                .Select(s => (Id: JsonHelpers.Str(s, "ID") ?? string.Empty, Volume: JsonHelpers.Str(s, "VolumeName") ?? string.Empty,
                    Date: DateTime.TryParse(JsonHelpers.Str(s, "Date"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue))
                .Where(s => s.Id.Length > 0)
                .ToList();

            foreach (var group in shadows.GroupBy(s => s.Volume, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = group.OrderByDescending(s => s.Date).ToList();
                long used = storage.TryGetValue(group.Key, out var u) ? u : 0;
                long perCopy = ordered.Count == 0 ? 0 : used / ordered.Count;
                foreach (var old in ordered.Skip(1))
                {
                    items.Add(new CleanupItem(group.Key, perCopy, false, old.Date)
                    {
                        DisplayName = $"Ponto de restauração de {old.Date.ToLocalTime():dd/MM/yyyy HH:mm}",
                        Tag = old.Id,
                        Reason = "Mantido o mais recente de cada disco.",
                    });
                }
            }
        }
        catch (JsonException)
        {
            return CleanupAnalysis.Unavailable(this, "Não foi possível listar os pontos de restauração.");
        }

        return new CleanupAnalysis(this, items, null,
            items.Count == 0 ? "Nenhum ponto de restauração antigo." : "Tamanho estimado (o Windows não informa por cópia).");
    }

    public override async Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new CleanupRunResult(this);
        execution.Log.Section($"{Name} [{Id}]");
        long before = DiskMeasure.FreeSpace(DiskMeasure.SystemDrive);
        foreach (var item in analysis.Items.Where(i => i.IsSelected && i.Tag is not null))
        {
            progress?.Report(new CleanupProgress(Name, item.Name, 0.5));
            var run = await CommandRunner.RunAsync(CommandRunner.SystemTool("vssadmin.exe"),
                $"delete shadows /shadow={item.Tag} /quiet", TimeSpan.FromMinutes(10), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            execution.Log.Item(run.Success ? "REMOVIDO" : "ERRO", item.Size, item.Name, run.Success ? null : run.Combined.Trim());
            if (!run.Success)
            {
                result.Success = false;
                result.Message = "Um ou mais pontos de restauração não puderam ser removidos.";
            }
        }

        result.BytesFreed = Math.Max(0, DiskMeasure.FreeSpace(DiskMeasure.SystemDrive) - before);
        return result;
    }
}

/// <summary>Remove versões antigas e duplicadas de pacotes de driver do DriverStore (pnputil).</summary>
public sealed class DriverStoreRule : CleanupRuleBase
{
    public DriverStoreRule()
        : base("sys.driverstore", CleanupCategory.Advanced, "Pacotes de driver antigos (DriverStore)",
            "Lista versões antigas de drivers de terceiros que foram substituídas por versões mais novas e as remove com " +
            "pnputil (drivers em uso não são removidos).", RiskLevel.Advanced)
    {
        RequiresAdmin = true;
        AlwaysPermanent = true;
        EnabledByDefault = false;
        ItemsSelectedByDefault = true;
    }

    public override async Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        if (!context.IsAdministrator)
        {
            return CleanupAnalysis.Unavailable(this, "Requer administrador.");
        }

        const string script =
            "Get-WindowsDriver -Online -ErrorAction Stop | Select-Object Driver, OriginalFileName, ClassName, ProviderName, " +
            "@{n='Date';e={$_.Date.ToString('o')}}, @{n='Version';e={$_.Version}} | ConvertTo-Json -Compress";
        var run = await CommandRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(5), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(run.Output))
        {
            return CleanupAnalysis.Unavailable(this, "Não foi possível listar os drivers (Get-WindowsDriver).");
        }

        var drivers = new List<(string Oem, string Original, string Class, string Provider, DateTime Date, Version Version)>();
        try
        {
            using var doc = JsonDocument.Parse(run.Output.Trim());
            var array = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new() { doc.RootElement };
            foreach (var d in array)
            {
                string oem = JsonHelpers.Str(d, "Driver") ?? string.Empty;
                string original = JsonHelpers.Str(d, "OriginalFileName") ?? string.Empty;
                if (!oem.StartsWith("oem", StringComparison.OrdinalIgnoreCase) || original.Length == 0)
                {
                    continue;
                }

                DateTime.TryParse(JsonHelpers.Str(d, "Date"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date);
                Version.TryParse(JsonHelpers.Str(d, "Version"), out var version);
                drivers.Add((oem, original, JsonHelpers.Str(d, "ClassName") ?? string.Empty,
                    JsonHelpers.Str(d, "ProviderName") ?? string.Empty, date, version ?? new Version(0, 0)));
            }
        }
        catch (JsonException)
        {
            return CleanupAnalysis.Unavailable(this, "Resposta inesperada ao listar drivers.");
        }

        var items = new List<CleanupItem>();
        foreach (var group in drivers.GroupBy(d => (Path.GetFileName(d.Original).ToLowerInvariant(), d.Provider.ToLowerInvariant(), d.Class.ToLowerInvariant())))
        {
            var ordered = group.OrderByDescending(d => d.Version).ThenByDescending(d => d.Date).ToList();
            foreach (var old in ordered.Skip(1))
            {
                string folder = Path.GetDirectoryName(old.Original) ?? string.Empty;
                long size = await Task.Run(() => DiskMeasure.DirectorySize(folder, cancellationToken), cancellationToken).ConfigureAwait(false);
                items.Add(new CleanupItem(folder, size, true, old.Date)
                {
                    DisplayName = $"{old.Provider} {old.Class} {old.Version} ({old.Date:dd/MM/yyyy}) — {old.Oem}",
                    Tag = old.Oem,
                    Reason = $"Existe versão mais nova: {ordered[0].Version} ({ordered[0].Oem}).",
                });
            }
        }

        return new CleanupAnalysis(this, items, null, items.Count == 0 ? "Nenhum driver antigo duplicado." : null);
    }

    public override async Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new CleanupRunResult(this);
        execution.Log.Section($"{Name} [{Id}]");
        foreach (var item in analysis.Items.Where(i => i.IsSelected && i.Tag is not null))
        {
            progress?.Report(new CleanupProgress(Name, item.Name, 0.5));
            var run = await CommandRunner.RunAsync(CommandRunner.SystemTool("pnputil.exe"), $"/delete-driver {item.Tag}",
                TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false);
            bool removed = run.Success && !Directory.Exists(item.Path);
            if (removed)
            {
                result.BytesFreed += item.Size;
            }

            execution.Log.Item(removed ? "DRIVER REMOVIDO" : "MANTIDO", item.Size, item.Name, removed ? null : run.Combined.Trim());
        }

        return result;
    }
}

/// <summary>Informativa: tamanho do pagefile.sys/swapfile.sys e como ajustar (nunca apaga).</summary>
public sealed class PagefileInfoRule : CleanupRuleBase
{
    public PagefileInfoRule()
        : base("sys.pagefile.info", CleanupCategory.Advanced, "Arquivo de paginação (informativo)",
            "Mostra o tamanho do pagefile.sys e do swapfile.sys. O Headroom nunca apaga esses arquivos: para reduzir, use " +
            "Sistema > Configurações avançadas do sistema > Desempenho > Avançado > Memória virtual.", RiskLevel.Advanced)
    {
        IsInformational = true;
        EnabledByDefault = false;
    }

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        var items = new List<CleanupItem>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            foreach (var name in new[] { "pagefile.sys", "swapfile.sys" })
            {
                string path = Path.Combine(drive.RootDirectory.FullName, name);
                long size = DiskMeasure.SafeFileLength(path);
                if (size > 0)
                {
                    items.Add(new CleanupItem(path, size, false, DateTime.Now) { IsSelected = false, Reason = "Somente informativo" });
                }
            }
        }

        return Task.FromResult(new CleanupAnalysis(this, items, null,
            "Somente informativo. Reduzir a memória virtual pode causar travamentos em programas pesados."));
    }

    public override Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new CleanupRunResult(this) { Message = "Regra informativa: nada foi alterado." });
}

/// <summary>Reconstrói o índice do Windows Search quando o Windows.edb/Windows.db está grande.</summary>
public sealed class SearchIndexRule : CleanupRuleBase
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Search", "Data", "Applications", "Windows");

    public SearchIndexRule()
        : base("sys.searchindex", CleanupCategory.Advanced, "Reconstruir índice do Windows Search",
            "Quando o banco do índice de pesquisa (Windows.edb) fica inchado, a reconstrução o recria do zero. " +
            "A pesquisa do Windows fica incompleta por algumas horas enquanto reindexa.", RiskLevel.Review)
    {
        RequiresAdmin = true;
        EnabledByDefault = false;
        AlwaysPermanent = true;
    }

    public override bool IsApplicable(CleanupContext context) => Directory.Exists(DataFolder);

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        if (!context.IsAdministrator)
        {
            return Task.FromResult(CleanupAnalysis.Unavailable(this, "Requer administrador."));
        }

        long size = DiskMeasure.SafeFileLength(Path.Combine(DataFolder, "Windows.edb")) +
                    DiskMeasure.SafeFileLength(Path.Combine(DataFolder, "Windows.db"));
        string note = size > 2L * ByteSize.GB
            ? $"Índice com {ByteSize.Format(size)} — reconstruir deve reduzir bastante."
            : $"Índice com {ByteSize.Format(size)} — tamanho normal; reconstruir só se a pesquisa estiver com problemas.";
        return Task.FromResult(CleanupAnalysis.Action(this, size / 2, note));
    }

    public override async Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new CleanupRunResult(this);
        execution.Log.Section($"{Name} [{Id}]");
        long before = DiskMeasure.FreeSpace(DiskMeasure.SystemDrive);
        try
        {
            progress?.Report(new CleanupProgress(Name, "Parando o serviço Windows Search", 0.2));
            await ServiceHelper.StopAsync(["WSearch"], cancellationToken).ConfigureAwait(false);
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Search", writable: true))
            {
                key?.SetValue("SetupCompletedSuccessfully", 0, RegistryValueKind.DWord);
            }

            execution.Log.Info("Índice marcado para reconstrução (SetupCompletedSuccessfully = 0).");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            result.Success = false;
            result.Message = "Não foi possível marcar o índice para reconstrução: " + ex.Message;
        }
        finally
        {
            progress?.Report(new CleanupProgress(Name, "Iniciando o serviço Windows Search", 0.8));
            await ServiceHelper.StartAsync(["WSearch"]).ConfigureAwait(false);
        }

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        result.BytesFreed = Math.Max(0, DiskMeasure.FreeSpace(DiskMeasure.SystemDrive) - before);
        return result;
    }
}

/// <summary>Leitura tolerante de JSON vindo do PowerShell (objeto único ou lista).</summary>
internal static class JsonHelpers
{
    public static IEnumerable<JsonElement> Array(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return System.Array.Empty<JsonElement>();
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().ToList(),
            JsonValueKind.Object => new[] { value },
            _ => System.Array.Empty<JsonElement>(),
        };
    }

    public static IEnumerable<JsonElement> Items(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Array => root.EnumerateArray().ToList(),
        JsonValueKind.Object => new[] { root },
        _ => System.Array.Empty<JsonElement>(),
    };

    public static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
                _ => null,
            }
            : null;

    public static long Long(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;

    public static bool Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;
}
