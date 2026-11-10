using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Converters;
using Headroom.App.Helpers;
using Headroom.App.Services;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;
using Wpf.Ui.Controls;

namespace Headroom.App.ViewModels;

/// <summary>Linha da árvore (lista "achatada": filhos são inseridos logo abaixo do pai ao expandir).</summary>
public sealed partial class TreeRowViewModel : ObservableObject, IFileItem
{
    private readonly FolderTreeViewModel _owner;

    public TreeRowViewModel(ScanNode node, int depth, FolderTreeViewModel owner)
    {
        ScanNode = node;
        Depth = depth;
        _owner = owner;
    }

    public ScanNode ScanNode { get; }
    public ScanNode? Node => ScanNode;
    public int Depth { get; }
    public string Name => ScanNode.Name;
    public string FullPath => ScanNode.FullPath;
    public bool IsDirectory => ScanNode.IsDirectory;
    public long Size => ScanNode.Size;
    public bool CanExpand => ScanNode.IsDirectory && ScanNode.HasChildren;
    public string SizeText => ByteSize.Format(ScanNode.Size);
    public double Fraction => ScanNode.FractionOfParent;
    public string PercentText => ByteSize.FormatPercent(ScanNode.FractionOfParent);
    public string FilesText => ScanNode.IsDirectory ? ByteSize.FormatCount(ScanNode.FileCount) : string.Empty;
    public DateTime Modified => ScanNode.LastModified;
    public Brush IconBrush => ScanNode.IsDirectory ? FolderBrush : CategoryBrushConverter.GetBrush(ScanNode.Category);

    public SymbolRegular Icon => ScanNode.IsDirectory
        ? (IsExpanded ? SymbolRegular.FolderOpen24 : SymbolRegular.Folder24)
        : SymbolRegular.Document24;

    private static readonly Brush FolderBrush = CreateFrozen(Color.FromRgb(0xE8, 0xB3, 0x39));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon))]
    private bool _isExpanded;

    /// <summary>Marcado para excluir (a raiz do scan não pode ser marcada).</summary>
    [ObservableProperty]
    private bool _isChecked;

    public bool CanCheck => Depth > 0;

    partial void OnIsCheckedChanged(bool value) => _owner.RefreshChecked();

    [RelayCommand]
    private void Toggle() => _owner.Toggle(this);

    private static Brush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Árvore de pastas virtualizada com colunas (Nome | Tamanho | % da pasta-pai | Arquivos | Modificado).
/// Os filhos são carregados sob demanda, só quando a pasta é expandida.
/// </summary>
public sealed partial class FolderTreeViewModel : FileActionsViewModel, IPageActivated
{
    private readonly HashSet<ScanNode> _expanded = new(ReferenceEqualityComparer.Instance);
    private ScanResult? _shownResult;
    private bool _dirty = true;

    public FolderTreeViewModel(AppServices services) : base(services)
    {
        services.Session.ResultChanged += (_, _) => _dirty = true;
    }

    public BulkObservableCollection<TreeRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private TreeRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _summary = string.Empty;

    public void OnActivated()
    {
        if (_dirty)
        {
            Rebuild();
        }
    }

    /// <summary>Remonta as linhas preservando as pastas que estavam expandidas.</summary>
    private void Rebuild()
    {
        _dirty = false;
        var result = Result;
        if (result is null)
        {
            Rows.ReplaceAll(Array.Empty<TreeRowViewModel>());
            return;
        }

        if (!ReferenceEquals(result, _shownResult))
        {
            _expanded.Clear();
            _expanded.Add(result.Root);
            _shownResult = result;
        }

        var rows = new List<TreeRowViewModel>();
        AddRecursive(result.Root, 0, rows);
        Rows.ReplaceAll(rows);
        RefreshChecked();
        Summary = $"{ByteSize.Format(result.TotalBytes)} em {ByteSize.FormatCount(result.FileCount)} arquivos e " +
                  $"{ByteSize.FormatCount(result.DirectoryCount)} pastas. Duplo clique (ou seta →) expande uma pasta.";
    }

    private void AddRecursive(ScanNode node, int depth, List<TreeRowViewModel> rows)
    {
        var row = new TreeRowViewModel(node, depth, this) { IsExpanded = _expanded.Contains(node) };
        rows.Add(row);
        if (!row.IsExpanded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            AddRecursive(child, depth + 1, rows);
        }
    }

    public void Toggle(TreeRowViewModel row)
    {
        if (!row.CanExpand)
        {
            return;
        }

        int index = Rows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        if (row.IsExpanded)
        {
            Collapse(row, index);
        }
        else
        {
            row.IsExpanded = true;
            _expanded.Add(row.ScanNode);
            var children = row.ScanNode.Children.Select(c => new TreeRowViewModel(c, row.Depth + 1, this)).ToList();
            Rows.InsertRange(index + 1, children);
        }

        SelectedRow = row;
    }

    public void Expand(TreeRowViewModel row)
    {
        if (row.CanExpand && !row.IsExpanded)
        {
            Toggle(row);
        }
    }

    public void CollapseOrGoToParent(TreeRowViewModel row)
    {
        int index = Rows.IndexOf(row);
        if (row.IsExpanded)
        {
            Collapse(row, index);
            return;
        }

        for (int i = index - 1; i >= 0; i--)
        {
            if (Rows[i].Depth < row.Depth)
            {
                SelectedRow = Rows[i];
                return;
            }
        }
    }

    private void Collapse(TreeRowViewModel row, int index)
    {
        row.IsExpanded = false;
        int end = index + 1;
        while (end < Rows.Count && Rows[end].Depth > row.Depth)
        {
            _expanded.Remove(Rows[end].ScanNode);
            end++;
        }

        _expanded.Remove(row.ScanNode);
        Rows.RemoveRange(index + 1, end - index - 1);

        // Linhas recolhidas deixam de contar como marcadas.
        RefreshChecked();
    }

    public override bool CanCheckAll => false;

    public override string CheckHint => "Marque arquivos ou pastas · Espaço marca a linha selecionada · botão direito para mais opções";

    /// <summary>Inverte a marcação da linha selecionada (tecla Espaço).</summary>
    public void ToggleChecked(TreeRowViewModel row)
    {
        if (row.CanCheck)
        {
            row.IsChecked = !row.IsChecked;
        }
    }

    protected override IReadOnlyList<IFileItem> GetCheckedItems() => Rows.Where(r => r.IsChecked).ToList();

    protected override void SetAllChecked(bool value)
    {
        foreach (var row in Rows)
        {
            row.IsChecked = value && row.CanCheck;
        }
    }

    [RelayCommand]
    private void CollapseAll()
    {
        if (Result is { } result)
        {
            _expanded.Clear();
            _expanded.Add(result.Root);
            Rebuild();
        }
    }

    protected override void OnItemsDeleted(IReadOnlyList<IFileItem> items, Core.Cleanup.Deletion.DeletionResult result) =>
        Rebuild();
}
