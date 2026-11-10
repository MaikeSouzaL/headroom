using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Helpers;
using Headroom.App.Services;
using Headroom.Core.Cleanup;
using Headroom.Core.Cleanup.Catalog;
using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Formatting;
using Headroom.Core.History;
using Headroom.Core.SystemTools;

namespace Headroom.App.ViewModels;

/// <summary>Item da prévia (arquivo/pasta/ação) com marcação individual.</summary>
public sealed partial class CleanupItemViewModel : ObservableObject, IFileItem
{
    private readonly CleanupRuleViewModel _owner;

    public CleanupItemViewModel(CleanupItem item, CleanupRuleViewModel owner)
    {
        Item = item;
        _owner = owner;
        _isSelected = item.IsSelected;
    }

    public CleanupItem Item { get; }
    public string Name => Item.Name;
    public string FullPath => Item.Path;
    public bool IsDirectory => Item.IsDirectory;
    public long Size => Item.Size;
    public Core.Scanning.ScanNode? Node => null;
    public string SizeText => Item.Size > 0 ? ByteSize.Format(Item.Size) : "—";
    public DateTime Modified => Item.LastModified;
    public string? Reason => Item.Reason;
    public string FilesText => Item.IsDirectory && Item.FileCount > 0 ? $"{ByteSize.FormatCount(Item.FileCount)} arquivos" : string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        Item.IsSelected = value;
        _owner.RefreshTotals();
    }
}

/// <summary>Uma regra na lista de limpeza.</summary>
public sealed partial class CleanupRuleViewModel : ObservableObject
{
    private static readonly Brush SafeBrush = Frozen("#2F9E44");
    private static readonly Brush ReviewBrush = Frozen("#E67700");
    private static readonly Brush AdvancedBrush = Frozen("#E03131");
    private readonly CleanupViewModel _owner;

    public CleanupRuleViewModel(ICleanupRule rule, bool isAdministrator, CleanupViewModel owner)
    {
        Rule = rule;
        _owner = owner;
        IsBlockedByAdmin = rule.RequiresAdmin && !isAdministrator && rule is Core.Cleanup.Rules.CommandRule;
    }

    public ICleanupRule Rule { get; }
    public string Name => Rule.Name;
    public string Description => Rule.Description;
    public string RiskText => CleanupCategoryInfo.RiskName(Rule.Risk);
    public Brush RiskBrush => Rule.Risk switch { RiskLevel.Safe => SafeBrush, RiskLevel.Review => ReviewBrush, _ => AdvancedBrush };
    public bool RequiresAdmin => Rule.RequiresAdmin;
    public bool IsBlockedByAdmin { get; }
    public bool IsPermanent => Rule.AlwaysPermanent && !Rule.IsInformational;
    public bool IsInformational => Rule.IsInformational;
    public bool HasProcesses => Rule.ProcessesToClose.Count > 0;
    public string ProcessesText => Rule.RequiresProcessesClosed
        ? "Fechar antes: " + string.Join(", ", Rule.ProcessesToClose)
        : "Recomendado fechar: " + string.Join(", ", Rule.ProcessesToClose);
    public bool IsJsonRule => Rule.Source != "Embutida";
    public string SourceText => IsJsonRule ? "Regra JSON: " + Path.GetFileName(Rule.Source) : string.Empty;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private CleanupAnalysis? _analysis;

    [ObservableProperty]
    private string _sizeText = "—";

    [ObservableProperty]
    private string _detailText = string.Empty;

    public ObservableCollection<CleanupItemViewModel> Items { get; } = new();

    public bool HasItems => Analysis is { Items.Count: > 0 };

    partial void OnIsCheckedChanged(bool value) => _owner.OnRuleCheckedChanged(this);

    public void SetAnalysis(CleanupAnalysis? analysis)
    {
        Analysis = analysis;
        Items.Clear();
        if (analysis is not null)
        {
            foreach (var item in analysis.Items)
            {
                Items.Add(new CleanupItemViewModel(item, this));
            }
        }

        RefreshTotals();
    }

    public void RefreshTotals()
    {
        if (Analysis is not { } a)
        {
            SizeText = "—";
            DetailText = string.Empty;
            return;
        }

        if (a.IsUnavailable)
        {
            SizeText = "—";
            DetailText = a.Note ?? "Indisponível.";
        }
        else if (a.IsAction)
        {
            SizeText = a.TotalBytes > 0 ? "~" + ByteSize.Format(a.TotalBytes) : "Ação";
            DetailText = a.Note ?? string.Empty;
        }
        else
        {
            SizeText = ByteSize.Format(a.SelectedBytes);
            DetailText = a.Items.Count == 0
                ? (a.Note ?? "Nada a limpar.")
                : $"{ByteSize.FormatCount(a.SelectedCount)} de {ByteSize.FormatCount(a.Items.Count)} itens selecionados" +
                  (a.SelectedBytes != a.TotalBytes ? $" (total encontrado: {ByteSize.Format(a.TotalBytes)})" : string.Empty) +
                  (string.IsNullOrEmpty(a.Note) ? string.Empty : " · " + a.Note);
        }

        _owner.RefreshTotals();
    }

    [RelayCommand]
    private void ShowItems() => _owner.SelectedRule = this;

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Categoria (grupo expansível) da lista de limpeza.</summary>
public sealed partial class CleanupCategoryViewModel : ObservableObject
{
    public CleanupCategoryViewModel(CleanupCategory category, IEnumerable<CleanupRuleViewModel> rules)
    {
        Category = category;
        Rules = new ObservableCollection<CleanupRuleViewModel>(rules);
    }

    public CleanupCategory Category { get; }
    public string Name => CleanupCategoryInfo.DisplayName(Category);
    public ObservableCollection<CleanupRuleViewModel> Rules { get; }

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private string _summary = string.Empty;

    public void Refresh()
    {
        int checkedCount = Rules.Count(r => r.IsChecked);
        long bytes = Rules.Where(r => r.IsChecked && r.Analysis is { IsUnavailable: false }).Sum(r => r.Analysis!.SelectedBytes);
        Summary = $"{checkedCount} de {Rules.Count} marcadas" + (bytes > 0 ? $" · {ByteSize.Format(bytes)}" : string.Empty);
    }

    [RelayCommand]
    private void CheckAll()
    {
        foreach (var rule in Rules.Where(r => !r.IsBlockedByAdmin && !r.IsInformational))
        {
            rule.IsChecked = true;
        }
    }

    [RelayCommand]
    private void UncheckAll()
    {
        foreach (var rule in Rules)
        {
            rule.IsChecked = false;
        }
    }
}

/// <summary>Linha do resumo final por categoria.</summary>
public sealed record SummaryLine(string Name, string Value);

/// <summary>
/// Tela de Limpeza: perfis, regras por categoria com risco, "Analisar" (prévia obrigatória),
/// itens selecionáveis, "Limpar" com Lixeira por padrão, progresso, cancelamento e resumo.
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject, IPageActivated
{
    private readonly AppServices _services;
    private bool _applyingProfile;
    private bool _loaded;
    private CancellationTokenSource? _cts;

    public CleanupViewModel(AppServices services)
    {
        _services = services;
        services.SettingsChanged += (_, _) => _loaded = false;
        _createRestorePoint = services.Settings.CreateRestorePointBeforeAdvanced;
    }

    public ObservableCollection<CleanupCategoryViewModel> Categories { get; } = new();
    public ObservableCollection<CleanupProfile> Profiles { get; } = new();
    public ObservableCollection<SummaryLine> SummaryLines { get; } = new();
    public bool IsAdministrator => _services.IsAdministrator;

    [ObservableProperty] private CleanupProfile? _selectedProfile;
    [ObservableProperty] private CleanupRuleViewModel? _selectedRule;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CleanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private bool _hasAnalysis;

    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _totalText = "Marque as regras e clique em \"Analisar\" para ver o tamanho de cada uma.";
    [ObservableProperty] private string _rulesInfo = string.Empty;
    [ObservableProperty] private bool _usePermanentDeletion;
    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _hasSummary;
    [ObservableProperty] private string _summaryHeadline = string.Empty;
    [ObservableProperty] private string _summaryDetails = string.Empty;
    [ObservableProperty] private string? _summaryLogPath;
    [ObservableProperty] private bool _summaryHasRecycleBin;
    [ObservableProperty] private string _loadWarnings = string.Empty;

    public void OnActivated()
    {
        if (!_loaded)
        {
            Load();
        }
    }

    private void Load()
    {
        _loaded = true;
        var catalog = _services.CreateRuleCatalog();
        var context = NewContext();
        var applicable = catalog.Rules.Where(r => SafeIsApplicable(r, context)).ToList();

        Categories.Clear();
        foreach (var category in CleanupCategoryInfo.Ordered)
        {
            var rules = applicable.Where(r => r.Category == category)
                .Select(r => new CleanupRuleViewModel(r, _services.IsAdministrator, this)).ToList();
            if (rules.Count > 0)
            {
                Categories.Add(new CleanupCategoryViewModel(category, rules)
                {
                    IsExpanded = category is CleanupCategory.System or CleanupCategory.Browsers,
                });
            }
        }

        RulesInfo = $"{applicable.Count} limpezas se aplicam a este PC (de {catalog.Rules.Count} conhecidas). " +
                    "Programas que não estão instalados ficam ocultos.";
        LoadWarnings = catalog.LoadErrors.Count == 0
            ? string.Empty
            : "Avisos nas regras JSON: " + string.Join(" | ", catalog.LoadErrors.Select(e => $"{Path.GetFileName(e.File)}: {e.Message}"));

        Profiles.Clear();
        foreach (var profile in CleanupProfiles.All(_services.Settings.CustomProfileRuleIds))
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == _services.Settings.SelectedProfileId) ?? Profiles[0];
        HasAnalysis = false;
        SelectedRule = null;
    }

    private static bool SafeIsApplicable(ICleanupRule rule, CleanupContext context)
    {
        try
        {
            return rule.IsApplicable(context);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private CleanupContext NewContext() =>
        new(_services.Settings, _services.IsAdministrator, _services.Session.Result);

    private IEnumerable<CleanupRuleViewModel> AllRules => Categories.SelectMany(c => c.Rules);

    partial void OnSelectedProfileChanged(CleanupProfile? value)
    {
        if (value is null)
        {
            return;
        }

        _applyingProfile = true;
        foreach (var rule in AllRules)
        {
            rule.IsChecked = !rule.IsBlockedByAdmin && value.Includes(rule.Rule);
        }

        _applyingProfile = false;
        RefreshTotals();
    }

    internal void OnRuleCheckedChanged(CleanupRuleViewModel rule)
    {
        if (!_applyingProfile && SelectedProfile?.Id != CleanupProfiles.Custom)
        {
            // Mudou à mão: passa a ser "Personalizada" (sem desfazer as marcações atuais).
            _applyingProfile = true;
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == CleanupProfiles.Custom);
            _applyingProfile = false;
        }

        RefreshTotals();
    }

    internal void RefreshTotals()
    {
        foreach (var category in Categories)
        {
            category.Refresh();
        }

        var analyzed = AllRules.Where(r => r.IsChecked && r.Analysis is { IsUnavailable: false }).ToList();
        int checkedCount = AllRules.Count(r => r.IsChecked);
        if (analyzed.Count == 0)
        {
            TotalText = $"{checkedCount} regra(s) marcada(s). Clique em \"Analisar\" para ver quanto cada uma libera.";
            return;
        }

        long total = analyzed.Sum(r => r.Analysis!.SelectedBytes);
        int pending = AllRules.Count(r => r.IsChecked && r.Analysis is null);
        TotalText = $"Total selecionado: {ByteSize.Format(total)} em {analyzed.Count} regra(s)" +
                    (pending > 0 ? $" · {pending} regra(s) marcada(s) ainda não analisadas" : string.Empty);
    }

    private bool CanAnalyze() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        var rules = AllRules.Where(r => r.IsChecked).ToList();
        if (rules.Count == 0)
        {
            await _services.Dialogs.InfoAsync("Nada marcado", "Marque pelo menos uma limpeza (ou escolha um perfil) antes de analisar.");
            return;
        }

        IsBusy = true;
        HasSummary = false;
        _cts = new CancellationTokenSource();
        foreach (var rule in rules)
        {
            rule.IsAnalyzing = true;
            rule.SetAnalysis(null);
        }

        var byRule = rules.ToDictionary(r => r.Rule);
        // Mostra as regras que ainda estão rodando (não a última que reportou, que pode já ter terminado).
        var running = new AnalysisProgressTracker();
        var progress = new Progress<CleanupProgress>(p =>
        {
            ProgressText = running.Update(p);
            ProgressValue = p.Fraction;
        });

        // Cada regra mostra o resultado assim que termina, sem esperar as demais.
        var completed = new Progress<CleanupAnalysis>(analysis =>
        {
            var vm = byRule[analysis.Rule];
            vm.IsAnalyzing = false;
            vm.SetAnalysis(analysis);
        });

        try
        {
            var engine = new CleanupEngine(NewContext());
            await engine.AnalyzeAsync(rules.Select(r => r.Rule), progress, _cts.Token, completed);
            HasAnalysis = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Análise cancelada.";
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Falha na análise", ex);
        }
        finally
        {
            foreach (var rule in rules)
            {
                rule.IsAnalyzing = false;
            }

            IsBusy = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
            RefreshTotals();
        }
    }

    private bool CanClean() => !IsBusy && HasAnalysis;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var ready = AllRules.Where(r => r.IsChecked && r.Analysis is { HasWork: true } && !r.IsInformational).ToList();
        var notAnalyzed = AllRules.Where(r => r.IsChecked && r.Analysis is null).ToList();
        if (ready.Count == 0)
        {
            await _services.Dialogs.InfoAsync("Nada para limpar",
                notAnalyzed.Count > 0 ? "Analise as regras marcadas antes de limpar." : "As regras marcadas não encontraram nada para limpar.");
            return;
        }

        long total = ready.Sum(r => r.Analysis!.SelectedBytes);
        bool hasAdvanced = ready.Any(r => r.Rule.Risk == RiskLevel.Advanced);
        bool hasPermanentRules = ready.Any(r => r.IsPermanent);
        var mode = UsePermanentDeletion ? DeletionMode.Permanent : DeletionMode.RecycleBin;

        string message =
            $"{ready.Count} limpeza(s), cerca de {ByteSize.Format(total)}.\n\n" +
            (mode == DeletionMode.RecycleBin
                ? "Os arquivos vão para a Lixeira (você pode restaurá-los). O espaço só é liberado ao esvaziar a Lixeira."
                : "EXCLUSÃO PERMANENTE: os arquivos NÃO vão para a Lixeira e não podem ser recuperados.") +
            (hasPermanentRules ? "\n\nAlgumas regras sempre excluem de forma permanente (ex.: Lixeira, Windows.old, comandos do sistema)." : string.Empty) +
            (hasAdvanced ? "\n\nHá itens AVANÇADOS. " + (CreateRestorePoint && IsAdministrator
                ? "Um ponto de restauração será criado antes."
                : "Nenhum ponto de restauração será criado.") : string.Empty) +
            (notAnalyzed.Count > 0 ? $"\n\n{notAnalyzed.Count} regra(s) marcada(s) sem análise serão ignoradas." : string.Empty);

        if (!await _services.Dialogs.ConfirmAsync("Confirmar limpeza", message,
                mode == DeletionMode.Permanent ? "Excluir permanentemente" : "Limpar", danger: mode == DeletionMode.Permanent || hasAdvanced))
        {
            return;
        }

        SaveCustomSelection();
        IsBusy = true;
        HasSummary = false;
        _cts = new CancellationTokenSource();
        var progress = new Progress<CleanupProgress>(p =>
        {
            ProgressText = $"{p.RuleName}: {p.Detail}";
            ProgressValue = p.Fraction;
        });

        try
        {
            var engine = new CleanupEngine(NewContext());
            var summary = await engine.CleanAsync(ready.Select(r => r.Analysis!).ToList(), new CleanupRunOptions
            {
                Mode = mode,
                CreateRestorePoint = CreateRestorePoint,
                IsAdministrator = IsAdministrator,
                OnProcessesRunning = AskAboutProcessesAsync,
            }, progress, _cts.Token);

            ShowSummary(summary);
            _ = Task.Run(() => _services.History.AddCleanup(new CleanupHistoryEntry(DateTime.Now, summary.TotalBytesFreed,
                summary.BytesMovedToRecycleBin, summary.Results.Count, summary.TotalSkipped, summary.TotalErrors, summary.LogPath, "Interface")));

            foreach (var rule in ready)
            {
                rule.SetAnalysis(null);
            }

            HasAnalysis = false;
        }
        catch (Exception ex)
        {
            await _services.Dialogs.ErrorAsync("Falha na limpeza", ex);
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
            RefreshTotals();
        }
    }

    private Task<ProcessDecision> AskAboutProcessesAsync(ICleanupRule rule, IReadOnlyList<RunningProcessInfo> running)
    {
        return Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            string names = string.Join(", ", running.Select(r => r.ExecutableName));
            string message = rule.RequiresProcessesClosed
                ? $"Para \"{rule.Name}\", feche: {names}.\n\nDeseja que o Headroom feche agora? Trabalhos não salvos nesses programas podem ser perdidos."
                : $"\"{rule.Name}\" funciona melhor com {names} fechado(s). Com o programa aberto, os arquivos em uso são pulados.";
            var choice = await _services.Dialogs.ChooseAsync("Programa aberto", message, "Fechar e continuar",
                rule.RequiresProcessesClosed ? null : "Continuar sem fechar", "Pular esta limpeza");
            return choice switch
            {
                DialogChoice.Primary => ProcessDecision.CloseAndContinue,
                DialogChoice.Secondary => ProcessDecision.ContinueWithout,
                _ => ProcessDecision.Skip,
            };
        }).Task.Unwrap();
    }

    private void ShowSummary(CleanupSummary summary)
    {
        SummaryHeadline = summary.Headline;
        SummaryLines.Clear();
        foreach (var (category, bytes) in summary.BytesByCategory.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value))
        {
            SummaryLines.Add(new SummaryLine(CleanupCategoryInfo.DisplayName(category), ByteSize.Format(bytes)));
        }

        var details = new List<string>
        {
            $"Itens pulados: {ByteSize.FormatCount(summary.TotalSkipped)} (em uso, sem permissão ou protegidos).",
            $"Erros: {summary.TotalErrors}. Duração: {ByteSize.FormatDuration(summary.Elapsed)}.",
        };
        details.AddRange(summary.SkippedRules.Select(s => "Pulada: " + s));
        details.AddRange(summary.Results.Where(r => !r.Success && r.Message is not null).Select(r => $"{r.Rule.Name}: {r.Message}"));
        if (summary.RestorePointMessage is not null)
        {
            details.Add(summary.RestorePointMessage);
        }

        SummaryDetails = string.Join("\n", details);
        SummaryLogPath = summary.LogPath;
        SummaryHasRecycleBin = summary.BytesMovedToRecycleBin > 0;
        HasSummary = true;
    }

    private void SaveCustomSelection()
    {
        var settings = _services.Settings.Clone();
        settings.CustomProfileRuleIds = AllRules.Where(r => r.IsChecked).Select(r => r.Rule.Id).ToList();
        settings.SelectedProfileId = SelectedProfile?.Id ?? CleanupProfiles.Quick;
        settings.CreateRestorePointBeforeAdvanced = CreateRestorePoint;
        _services.SaveSettings(settings);
        _loaded = true; // salvar não deve recarregar a lista no meio do trabalho
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    async partial void OnUsePermanentDeletionChanged(bool value)
    {
        if (!value)
        {
            return;
        }

        bool ok = await _services.Dialogs.ConfirmAsync("Exclusão permanente",
            "Com esta opção os arquivos NÃO vão para a Lixeira e não podem ser recuperados. Tem certeza?",
            "Sim, excluir permanentemente", danger: true);
        if (!ok)
        {
            UsePermanentDeletion = false;
        }
    }

    [RelayCommand]
    private void CloseItems() => SelectedRule = null;

    [RelayCommand]
    private void SelectAllItems()
    {
        if (SelectedRule is null)
        {
            return;
        }

        foreach (var item in SelectedRule.Items)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNoItems()
    {
        if (SelectedRule is null)
        {
            return;
        }

        foreach (var item in SelectedRule.Items)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand]
    private void OpenItemInExplorer(CleanupItemViewModel? item)
    {
        if (item is not null && (File.Exists(item.FullPath) || Directory.Exists(item.FullPath)))
        {
            _services.Shell.OpenInExplorer(item.FullPath);
        }
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (SummaryLogPath is not null && File.Exists(SummaryLogPath))
        {
            _services.Shell.OpenUrlOrFile(SummaryLogPath);
        }
    }

    [RelayCommand]
    private async Task EmptyRecycleBinAsync()
    {
        var rule = new Core.Cleanup.Rules.RecycleBinRule();
        var context = NewContext();
        var analysis = await rule.AnalyzeAsync(context, CancellationToken.None);
        if (analysis.TotalBytes == 0)
        {
            await _services.Dialogs.InfoAsync("Lixeira", "A Lixeira já está vazia.");
            return;
        }

        if (!await _services.Dialogs.ConfirmAsync("Esvaziar a Lixeira",
                $"Excluir definitivamente {ByteSize.Format(analysis.TotalBytes)} da Lixeira de todos os discos?", "Esvaziar", danger: true))
        {
            return;
        }

        using var log = Core.Cleanup.Logging.CleanupLog.Create("lixeira");
        var result = await rule.CleanAsync(analysis, new CleanupExecution(DeletionMode.Permanent, log, IsAdministrator), null, CancellationToken.None);
        SummaryHasRecycleBin = false;
        await _services.Dialogs.InfoAsync("Lixeira esvaziada", $"{ByteSize.Format(result.BytesFreed)} liberados.");
    }

    [RelayCommand]
    private void Reload() => Load();
}
