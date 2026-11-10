using System.Runtime.InteropServices;
using System.Text.Json;
using Headroom.Core.Settings;
using Microsoft.Win32;

namespace Headroom.Core.Optimization;

/// <summary>Um valor do registro alterado por um ajuste.</summary>
/// <param name="KeyPath">Caminho sob HKEY_CURRENT_USER.</param>
/// <param name="Optimized">Valor aplicado pelo ajuste (int para DWORD, string para REG_SZ).</param>
/// <param name="WindowsDefault">Valor padrão do Windows (null = o valor não existe por padrão).</param>
public sealed record TweakValue(string KeyPath, string Name, object Optimized, object? WindowsDefault)
{
    public RegistryValueKind Kind => Optimized is int ? RegistryValueKind.DWord : RegistryValueKind.String;
}

/// <summary>
/// Ajuste de desempenho reversível. Só mexe em HKEY_CURRENT_USER (não precisa de administrador) e guarda os
/// valores anteriores antes de aplicar, para "Desfazer" voltar exatamente ao que era.
/// </summary>
public sealed class PerformanceTweak
{
    public PerformanceTweak(string id, string name, string description, string whenItApplies, IReadOnlyList<TweakValue> values,
        Action<bool>? applyNow = null)
    {
        Id = id;
        Name = name;
        Description = description;
        WhenItApplies = whenItApplies;
        Values = values;
        ApplyNow = applyNow;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }

    /// <summary>Completa a frase "Vale: …" (ex.: "na hora", "no próximo jogo aberto").</summary>
    public string WhenItApplies { get; }

    public IReadOnlyList<TweakValue> Values { get; }

    /// <summary>Avisa o Windows para usar o valor sem reiniciar (quando possível). Recebe true ao aplicar.</summary>
    private Action<bool>? ApplyNow { get; }

    /// <summary>Raiz usada para ler/gravar (HKCU; os testes usam uma subchave própria).</summary>
    internal RegistryKey Root { get; init; } = Registry.CurrentUser;

    /// <summary>Valor ausente conta como o padrão do Windows.</summary>
    public bool IsApplied => Values.All(v => Equals(Normalize(Read(v) ?? v.WindowsDefault), Normalize(v.Optimized)));

    public void Apply(TweakBackupStore backup)
    {
        if (!backup.Has(Id))
        {
            backup.Save(Id, Values.Select(v => new TweakBackupValue(v.KeyPath, v.Name, Serialize(Read(v)), v.Kind)).ToList());
        }

        foreach (var value in Values)
        {
            Write(value.KeyPath, value.Name, value.Optimized, value.Kind);
        }

        RunApplyNow(true);
    }

    public void Revert(TweakBackupStore backup)
    {
        var saved = backup.Get(Id);
        foreach (var value in Values)
        {
            var previous = saved?.FirstOrDefault(s => s.KeyPath == value.KeyPath && s.Name == value.Name);
            object? target = previous is not null ? Deserialize(previous) : value.WindowsDefault;
            if (target is null)
            {
                Delete(value.KeyPath, value.Name);
            }
            else
            {
                Write(value.KeyPath, value.Name, target, target is int ? RegistryValueKind.DWord : RegistryValueKind.String);
            }
        }

        backup.Remove(Id);
        RunApplyNow(false);
    }

    private void RunApplyNow(bool applied)
    {
        try
        {
            ApplyNow?.Invoke(applied);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // O valor já está gravado; vale na próxima vez que o Windows o ler.
        }
    }

    private object? Read(TweakValue value)
    {
        using var key = Root.OpenSubKey(value.KeyPath);
        return key?.GetValue(value.Name);
    }

    private void Write(string keyPath, string name, object data, RegistryValueKind kind)
    {
        using var key = Root.CreateSubKey(keyPath, writable: true);
        key.SetValue(name, data, kind);
    }

    private void Delete(string keyPath, string name)
    {
        using var key = Root.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static object? Normalize(object? value) => value switch
    {
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string s => s.Trim(),
        _ => value,
    };

    private static string? Serialize(object? value) => value switch
    {
        null => null,
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static object? Deserialize(TweakBackupValue saved) => saved.Data is null
        ? null
        : saved.Kind == RegistryValueKind.DWord && int.TryParse(saved.Data, out int number) ? number : saved.Data;
}

/// <summary>Valor anterior guardado antes de aplicar um ajuste (Data null = não existia).</summary>
public sealed record TweakBackupValue(string KeyPath, string Name, string? Data, RegistryValueKind Kind);

/// <summary>Backups dos ajustes em %LOCALAPPDATA%\Headroom\ajustes-backup.json.</summary>
public sealed class TweakBackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _file;
    private readonly Dictionary<string, List<TweakBackupValue>> _data;

    public TweakBackupStore(string? file = null)
    {
        _file = file ?? Path.Combine(AppPaths.DataDirectory, "ajustes-backup.json");
        _data = Load(_file);
    }

    public bool Has(string id) => _data.ContainsKey(id);

    public IReadOnlyList<TweakBackupValue>? Get(string id) => _data.TryGetValue(id, out var list) ? list : null;

    public void Save(string id, List<TweakBackupValue> values)
    {
        _data[id] = values;
        Persist();
    }

    public void Remove(string id)
    {
        if (_data.Remove(id))
        {
            Persist();
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions));
        File.Move(temp, _file, overwrite: true);
    }

    private static Dictionary<string, List<TweakBackupValue>> Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<TweakBackupValue>>>(File.ReadAllText(file))
                       ?? new Dictionary<string, List<TweakBackupValue>>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Dictionary<string, List<TweakBackupValue>>();
    }
}

/// <summary>
/// Catálogo de ajustes seguros e conhecidos. Nenhum desliga segurança, atualizações ou serviços do Windows —
/// só reduz efeitos visuais, atrasos e coisas que rodam em segundo plano sem o usuário pedir.
/// </summary>
public static class PerformanceTweaks
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    public static IReadOnlyList<PerformanceTweak> All { get; } = new[]
    {
        new PerformanceTweak("visual.transparency", "Desligar transparência",
            "Tira o efeito translúcido da barra de tarefas, do Iniciar e da Central de Ações. Alivia a placa de vídeo em PCs modestos.",
            "na hora",
            new[] { new TweakValue(Personalize, "EnableTransparency", 0, 1) },
            _ => Native.BroadcastSettingChange("ImmersiveColorSet")),

        new PerformanceTweak("visual.animations", "Desligar animação ao minimizar/maximizar",
            "Janelas abrem e fecham na hora, sem a animação de encolher/crescer.",
            "na hora",
            new[] { new TweakValue(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1") },
            applied => Native.SetMinimizeAnimation(!applied)),

        new PerformanceTweak("visual.menudelay", "Menus abrem mais rápido",
            "Reduz a espera para abrir submenus de 400 ms para 100 ms.",
            "na hora",
            new[] { new TweakValue(@"Control Panel\Desktop", "MenuShowDelay", "100", "400") },
            applied => Native.SetMenuShowDelay(applied ? 100 : 400)),

        new PerformanceTweak("startup.delay", "Programas de inicialização sem atraso",
            "O Windows espera alguns segundos antes de abrir os programas de inicialização; isto remove a espera.",
            "na próxima vez que ligar o PC",
            new[] { new TweakValue(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, null) }),

        new PerformanceTweak("background.apps", "Bloquear apps da Store em segundo plano",
            "Impede que apps da Microsoft Store (Clima, Notícias, Fotos...) rodem escondidos gastando memória e bateria. Os apps continuam funcionando quando abertos.",
            "na hora (Windows 10; no Windows 11 use Configurações > Apps)",
            new[]
            {
                new TweakValue(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, null),
                new TweakValue(@"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0, null),
            }),

        new PerformanceTweak("games.gamedvr", "Desligar gravação em segundo plano do Xbox Game Bar",
            "A Game Bar grava os jogos o tempo todo para \"capturar o que aconteceu\"; desligar melhora o FPS em PCs mais fracos.",
            "no próximo jogo aberto",
            new[]
            {
                new TweakValue(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                new TweakValue(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, null),
            }),

        new PerformanceTweak("windows.suggestions", "Sem sugestões e apps promovidos",
            "Desliga dicas, sugestões no Iniciar e a instalação automática de apps patrocinados (Candy Crush e afins).",
            "na hora",
            new[]
            {
                new TweakValue(ContentDelivery, "SilentInstalledAppsEnabled", 0, 1),
                new TweakValue(ContentDelivery, "SystemPaneSuggestionsEnabled", 0, 1),
                new TweakValue(ContentDelivery, "SoftLandingEnabled", 0, 1),
                new TweakValue(ContentDelivery, "SubscribedContent-338388Enabled", 0, 1),
                new TweakValue(ContentDelivery, "SubscribedContent-338389Enabled", 0, 1),
            }),

        new PerformanceTweak("search.web", "Pesquisa do Iniciar só no PC",
            "A busca do menu Iniciar deixa de consultar a internet (Bing) a cada letra digitada; os resultados aparecem mais rápido.",
            "ao reiniciar o Explorer ou entrar de novo no Windows",
            new[]
            {
                new TweakValue(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, null),
                new TweakValue(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),
            }),
    };

    private static class Native
    {
        private const uint SpiSetAnimation = 0x0049;
        private const uint SpiSetMenuShowDelay = 0x006B;
        private const uint SpifUpdateIniFile = 0x01;
        private const uint SpifSendChange = 0x02;
        private const uint WmSettingChange = 0x001A;
        private static readonly IntPtr HwndBroadcast = new(0xFFFF);

        public static void SetMinimizeAnimation(bool enabled)
        {
            var info = new AnimationInfo { cbSize = (uint)Marshal.SizeOf<AnimationInfo>(), iMinAnimate = enabled ? 1 : 0 };
            SystemParametersInfo(SpiSetAnimation, info.cbSize, ref info, SpifUpdateIniFile | SpifSendChange);
        }

        public static void SetMenuShowDelay(int milliseconds) =>
            SystemParametersInfo(SpiSetMenuShowDelay, (uint)milliseconds, IntPtr.Zero, SpifUpdateIniFile | SpifSendChange);

        public static void BroadcastSettingChange(string area) =>
            SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, area, 0x0002 /* SMTO_ABORTIFHUNG */, 2000, out _);

        [StructLayout(LayoutKind.Sequential)]
        private struct AnimationInfo
        {
            public uint cbSize;
            public int iMinAnimate;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref AnimationInfo info, uint winIni);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, IntPtr info, uint winIni);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags,
            uint timeout, out IntPtr result);
    }
}
