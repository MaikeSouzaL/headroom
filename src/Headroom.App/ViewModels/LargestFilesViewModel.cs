using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Converters;
using Headroom.App.Helpers;
using Headroom.App.Services;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;

namespace Headroom.App.ViewModels;

/// <summary>Linha da grade de maiores arquivos.</summary>
public sealed partial class FileRowViewModel : ObservableObject, IFileItem
{
    private readonly FileActionsViewModel? _owner;

    public FileRowViewModel(ScanNode node, FileActionsViewModel? owner = null)
    {
        ScanNode = node;
        Folder = node.ParentPath;
        FullPath = node.FullPath;
        _owner = owner;
    }

    /// <summary>Marcado para excluir.</summary>
    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _owner?.RefreshChecked();

    public ScanNode ScanNode { get; }
    public ScanNode? Node => ScanNode;
    public string Name => ScanNode.Name;
    public string Folder { get; }
    public string FullPath { get; }
    public bool IsDirectory => false;
    public long Size => ScanNode.Size;
    public string SizeText => ByteSize.Format(ScanNode.Size);
    public FileCategory Category => ScanNode.Category;
    public bool IsInRecycleBin => RecycleBinPath.Contains(FullPath);

    public string TypeText => (string.IsNullOrEmpty(ScanNode.Extension)
            ? FileCategoryInfo.DisplayName(ScanNode.Category)
            : $"{FileCategoryInfo.DisplayName(ScanNode.Category)} ({ScanNode.Extension.ToLowerInvariant()})") +
        (IsInRecycleBin ? " · na Lixeira" : string.Empty);
    public Brush CategoryBrush => CategoryBrushConverter.GetBrush(ScanNode.Category);
    public DateTime Modified => ScanNode.LastModified;
}

/// <summary>Opção do filtro de categoria ("Todas" = null).</summary>
public sealed record CategoryOption(string Name, FileCategory? Category);

/// <summary>
/// Maiores arquivos encontrados no scan (N configurável), com busca, filtro por categoria,
/// tamanho mínimo e "não modificado há mais de X meses".
/// </summary>
public sealed partial class LargestFilesViewModel : FileActionsViewModel, IPageActivated
{
    private bool _dirty = true;
    private List<FileRowViewModel> _all = new();

    public LargestFilesViewModel(AppServices services) : base(services)
    {
        services.Session.ResultChanged += (_, _) => _dirty = true;
        CategoryOptions = new[] { new CategoryOption("Todas as categorias", null) }
            .Concat(FileCategoryInfo.All.Select(c => new CategoryOption(FileCategoryInfo.DisplayName(c), c)))
            .ToList();
        _selectedCategory = CategoryOptions[0];
        _minimumSizeMb = Math.Round(services.Settings.LargeFileMinimumBytes / (double)ByteSize.MB);
    }

    public IReadOnlyList<CategoryOption> CategoryOptions { get; }

    public BulkObservableCollection<FileRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private CategoryOption _selectedCategory;
    [ObservableProperty] private double _minimumSizeMb;
    [ObservableProperty] private double _olderThanMonths;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _capacityNote = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(CategoryOption value) => ApplyFilter();
    partial void OnMinimumSizeMbChanged(double value) => ApplyFilter();
    partial void OnOlderThanMonthsChanged(double value) => ApplyFilter();

    public void OnActivated()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        var result = Result;
        _all = result is null ? new List<FileRowViewModel>() : result.LargestFiles.Select(f => new FileRowViewModel(f, this)).ToList();
        CapacityNote = result is null
            ? string.Empty
            : $"O scan guarda os {ByteSize.FormatCount(result.TopFilesCapacity)} maiores arquivos (altere em Configurações).";
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedCategory = CategoryOptions[0];
        MinimumSizeMb = 0;
        OlderThanMonths = 0;
    }

    private void ApplyFilter()
    {
        string search = SearchText.Trim();
        long minBytes = (long)(Math.Max(0, MinimumSizeMb) * ByteSize.MB);
        DateTime? olderThan = OlderThanMonths > 0 ? DateTime.Now.AddMonths(-(int)OlderThanMonths) : null;
        var category = SelectedCategory?.Category;

        var filtered = _all.Where(r =>
                r.Size >= minBytes &&
                (category is null || r.Category == category) &&
                (olderThan is null || r.Modified < olderThan) &&
                (search.Length == 0 || r.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Rows.ReplaceAll(filtered);
        Summary = $"{ByteSize.FormatCount(filtered.Count)} arquivo(s) · {ByteSize.Format(filtered.Sum(r => r.Size))}";

        // Só o que está visível conta como marcado: um filtro nunca esconde algo que será apagado.
        RefreshChecked();
    }

    /// <summary>Inverte a marcação das linhas selecionadas (tecla Espaço).</summary>
    public void ToggleChecked(System.Collections.IList selected)
    {
        var rows = selected.OfType<FileRowViewModel>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        bool value = !rows.All(r => r.IsChecked);
        BatchChecked(() => rows.ForEach(r => r.IsChecked = value));
    }

    protected override IReadOnlyList<IFileItem> GetCheckedItems() => Rows.Where(r => r.IsChecked).ToList();

    protected override void SetAllChecked(bool value)
    {
        // Marcar: só as visíveis. Desmarcar: todas, inclusive as escondidas pelo filtro.
        foreach (var row in value ? (IEnumerable<FileRowViewModel>)Rows : _all)
        {
            row.IsChecked = value;
        }
    }

    protected override void OnItemsDeleted(IReadOnlyList<IFileItem> items, Core.Cleanup.Deletion.DeletionResult result)
    {
        var gone = items.Where(i => !File.Exists(i.FullPath)).Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _all.RemoveAll(r => gone.Contains(r.FullPath));
        ApplyFilter();
    }
}
