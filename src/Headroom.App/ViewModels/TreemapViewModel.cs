using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Services;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;

namespace Headroom.App.ViewModels;

/// <summary>Item do caminho de navegação (breadcrumb) do treemap.</summary>
public sealed record BreadcrumbItem(string Name, ScanNode Node, bool IsLast);

/// <summary>Mapa de espaço (treemap): clique entra na pasta, breadcrumb volta.</summary>
public sealed partial class TreemapViewModel : FileActionsViewModel, IPageActivated
{
    private ScanResult? _shownResult;

    public TreemapViewModel(AppServices services) : base(services)
    {
        services.Session.ResultChanged += (_, _) => OnResultChanged();
        Legend = FileCategoryInfo.All
            .Select(c => new CategoryLegendItem(FileCategoryInfo.DisplayName(c),
                Converters.CategoryBrushConverter.GetBrush(c), string.Empty, string.Empty, string.Empty))
            .ToList();
    }

    public IReadOnlyList<CategoryLegendItem> Legend { get; }

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    private ScanNode? _currentNode;

    [ObservableProperty]
    private ScanNode? _hoveredNode;

    [ObservableProperty]
    private ScanNode? _contextNode;

    [ObservableProperty]
    private int _renderVersion;

    [ObservableProperty]
    private string _hoverText = "Passe o mouse sobre o mapa para ver detalhes. Clique para entrar em uma pasta.";

    [ObservableProperty]
    private string _currentInfo = string.Empty;

    public void OnActivated()
    {
        if (Result is { } result && !ReferenceEquals(result, _shownResult))
        {
            _shownResult = result;
            NavigateTo(result.Root);
        }
    }

    private void OnResultChanged()
    {
        if (Result is not { } result)
        {
            return;
        }

        if (!ReferenceEquals(result, _shownResult))
        {
            return; // novo scan: será tratado em OnActivated
        }

        // Itens apagados: se a pasta atual saiu da árvore, volta para a raiz.
        if (CurrentNode is not null && !CurrentNode.IsRoot && !result.Root.IsAncestorOf(CurrentNode))
        {
            NavigateTo(result.Root);
        }
        else
        {
            UpdateInfo();
            RenderVersion++;
        }
    }

    partial void OnHoveredNodeChanged(ScanNode? value)
    {
        HoverText = value is null
            ? "Passe o mouse sobre o mapa para ver detalhes. Clique para entrar em uma pasta."
            : $"{value.FullPath} — {ByteSize.Format(value.Size)}" +
              (value.IsDirectory ? $" · {ByteSize.FormatCount(value.FileCount)} arquivos" : $" · {FileCategoryInfo.DisplayName(value.Category)}");
    }

    [RelayCommand]
    private void NavigateTo(ScanNode? node)
    {
        if (node is null || !node.IsDirectory)
        {
            return;
        }

        CurrentNode = node;
        Breadcrumbs.Clear();
        var chain = node.Ancestors().Reverse().Append(node).ToList();
        for (int i = 0; i < chain.Count; i++)
        {
            var n = chain[i];
            Breadcrumbs.Add(new BreadcrumbItem(n.IsRoot ? n.Name : n.Name, n, i == chain.Count - 1));
        }

        UpdateInfo();
    }

    private bool CanGoUp() => CurrentNode?.Parent is not null;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp() => NavigateTo(CurrentNode?.Parent);

    [RelayCommand]
    private void OpenContextFolder()
    {
        if (ContextNode is { IsDirectory: true } node)
        {
            NavigateTo(node);
        }
    }

    private void UpdateInfo()
    {
        if (CurrentNode is { } node)
        {
            CurrentInfo = $"{ByteSize.Format(node.Size)} · {ByteSize.FormatCount(node.FileCount)} arquivos · {node.Children.Count(c => c.IsDirectory)} subpastas";
        }
    }
}
