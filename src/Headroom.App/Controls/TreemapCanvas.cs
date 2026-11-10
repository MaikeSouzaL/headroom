using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;
using Headroom.Core.Treemap;

namespace Headroom.App.Controls;

/// <summary>
/// Treemap clicável estilo WinDirStat/WizTree desenhado à mão em um <see cref="Canvas"/>.
/// O layout usa o algoritmo squarified (<see cref="SquarifiedLayout"/>) de forma aninhada:
/// pastas viram molduras com cabeçalho e arquivos são coloridos pela categoria. O desenho fica
/// em cache (DrawingGroup); passar o mouse só redesenha o realce.
/// </summary>
public sealed class TreemapCanvas : Canvas
{
    private const int MaxRects = 40_000;
    private const double HeaderHeight = 17;

    public static readonly DependencyProperty RootNodeProperty = DependencyProperty.Register(
        nameof(RootNode), typeof(ScanNode), typeof(TreemapCanvas),
        new FrameworkPropertyMetadata(null, (d, _) => ((TreemapCanvas)d).InvalidateLayoutCache()));

    public static readonly DependencyProperty RenderVersionProperty = DependencyProperty.Register(
        nameof(RenderVersion), typeof(int), typeof(TreemapCanvas),
        new FrameworkPropertyMetadata(0, (d, _) => ((TreemapCanvas)d).InvalidateLayoutCache()));

    public static readonly DependencyProperty MaxDepthProperty = DependencyProperty.Register(
        nameof(MaxDepth), typeof(int), typeof(TreemapCanvas),
        new FrameworkPropertyMetadata(16, (d, _) => ((TreemapCanvas)d).InvalidateLayoutCache()));

    public static readonly DependencyProperty HoveredNodeProperty = DependencyProperty.Register(
        nameof(HoveredNode), typeof(ScanNode), typeof(TreemapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ContextNodeProperty = DependencyProperty.Register(
        nameof(ContextNode), typeof(ScanNode), typeof(TreemapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty NavigateCommandProperty = DependencyProperty.Register(
        nameof(NavigateCommand), typeof(ICommand), typeof(TreemapCanvas));

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush DirectoryFill = Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0x35, 0x3B)));
    private static readonly Brush HeaderFill = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2B)));
    private static readonly Brush CollapsedFill = Frozen(new SolidColorBrush(Color.FromRgb(0x6C, 0x75, 0x7D)));
    private static readonly Brush HeaderText = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEB)));
    private static readonly Brush DarkText = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x14)));
    private static readonly Pen DirectoryPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x19)), 1));
    private static readonly Pen HighlightPen = Frozen(new Pen(Brushes.White, 2));
    private static readonly Pen HighlightOuterPen = Frozen(new Pen(Brushes.Black, 4));
    private static readonly Dictionary<FileCategory, (Brush Fill, Pen Border)> FileStyles = BuildFileStyles();

    private readonly List<HitRect> _hits = new(4096);
    private readonly TextBlock _tooltipText = new() { MaxWidth = 520, TextWrapping = TextWrapping.Wrap };
    private DrawingGroup? _cache;
    private Size _cacheSize;
    private int _rectCount;
    private Rect? _highlight;

    public TreemapCanvas()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent; // necessário para receber eventos de mouse
        Focusable = true;
        ToolTip = new ToolTip { Content = _tooltipText };
        ToolTipService.SetInitialShowDelay(this, 350);
        ToolTipService.SetShowDuration(this, 60_000);
        SizeChanged += (_, _) => InvalidateLayoutCache();
    }

    public ScanNode? RootNode
    {
        get => (ScanNode?)GetValue(RootNodeProperty);
        set => SetValue(RootNodeProperty, value);
    }

    /// <summary>Incrementado pelo ViewModel para forçar novo layout (ex.: depois de apagar itens).</summary>
    public int RenderVersion
    {
        get => (int)GetValue(RenderVersionProperty);
        set => SetValue(RenderVersionProperty, value);
    }

    public int MaxDepth
    {
        get => (int)GetValue(MaxDepthProperty);
        set => SetValue(MaxDepthProperty, value);
    }

    public ScanNode? HoveredNode
    {
        get => (ScanNode?)GetValue(HoveredNodeProperty);
        set => SetValue(HoveredNodeProperty, value);
    }

    /// <summary>Item sob o mouse no clique direito (alvo do menu de contexto).</summary>
    public ScanNode? ContextNode
    {
        get => (ScanNode?)GetValue(ContextNodeProperty);
        set => SetValue(ContextNodeProperty, value);
    }

    /// <summary>Executado com a pasta clicada (entrar na pasta).</summary>
    public ICommand? NavigateCommand
    {
        get => (ICommand?)GetValue(NavigateCommandProperty);
        set => SetValue(NavigateCommandProperty, value);
    }

    private void InvalidateLayoutCache()
    {
        _cache = null;
        _highlight = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = new Size(ActualWidth, ActualHeight);
        if (RootNode is null || size.Width < 4 || size.Height < 4)
        {
            return;
        }

        if (_cache is null || _cacheSize != size)
        {
            _cache = BuildDrawing(RootNode, size);
            _cacheSize = size;
        }

        dc.DrawDrawing(_cache);

        if (_highlight is { } rect)
        {
            dc.DrawRectangle(null, HighlightOuterPen, rect);
            dc.DrawRectangle(null, HighlightPen, rect);
        }
    }

    private DrawingGroup BuildDrawing(ScanNode root, Size size)
    {
        _hits.Clear();
        _rectCount = 0;
        var group = new DrawingGroup();
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        using (var dc = group.Open())
        {
            var bounds = new Rect(0, 0, size.Width, size.Height);
            if (root.Size <= 0)
            {
                dc.DrawRectangle(DirectoryFill, null, bounds);
                DrawLabel(dc, "Pasta vazia", bounds, HeaderText, 13, pixelsPerDip);
            }
            else
            {
                RenderDirectoryContent(dc, root, bounds, 0, pixelsPerDip);
            }
        }

        group.Freeze();
        return group;
    }

    /// <summary>Desenha os filhos de uma pasta dentro do retângulo dado.</summary>
    private void RenderDirectoryContent(DrawingContext dc, ScanNode directory, Rect area, int depth, double pixelsPerDip)
    {
        var children = directory.Children;
        int count = 0;
        while (count < children.Count && children[count].Size > 0)
        {
            count++; // os filhos estão ordenados do maior para o menor
        }

        if (count == 0)
        {
            return;
        }

        var sizes = new double[count];
        for (int i = 0; i < count; i++)
        {
            sizes[i] = children[i].Size;
        }

        var rects = SquarifiedLayout.Compute(sizes, new LayoutRect(area.X, area.Y, area.Width, area.Height));
        for (int i = 0; i < count; i++)
        {
            if (_rectCount >= MaxRects)
            {
                return;
            }

            var r = rects[i];
            if (r.Width < 1 || r.Height < 1)
            {
                // Como estão em ordem decrescente, os próximos são ainda menores.
                if (r.Width * r.Height < 0.5)
                {
                    break;
                }

                continue;
            }

            var rect = new Rect(r.X, r.Y, r.Width, r.Height);
            var child = children[i];
            if (child.IsDirectory)
            {
                RenderDirectory(dc, child, rect, depth + 1, pixelsPerDip);
            }
            else
            {
                RenderFile(dc, child, rect, pixelsPerDip);
            }
        }
    }

    private void RenderDirectory(DrawingContext dc, ScanNode directory, Rect rect, int depth, double pixelsPerDip)
    {
        _rectCount++;
        _hits.Add(new HitRect(rect, directory));

        bool tiny = rect.Width < 6 || rect.Height < 6 || depth >= MaxDepth;
        if (tiny)
        {
            dc.DrawRectangle(CollapsedFill, DirectoryPen, rect);
            return;
        }

        dc.DrawRectangle(DirectoryFill, DirectoryPen, rect);

        Rect content;
        if (rect.Height > HeaderHeight + 10 && rect.Width > 36)
        {
            var header = new Rect(rect.X, rect.Y, rect.Width, HeaderHeight);
            dc.DrawRectangle(HeaderFill, null, header);
            string text = $"{directory.Name}  {ByteSize.Format(directory.Size)}";
            DrawLabel(dc, text, header, HeaderText, 11, pixelsPerDip);
            content = new Rect(rect.X + 2, rect.Y + HeaderHeight, Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - HeaderHeight - 2));
        }
        else
        {
            content = new Rect(rect.X + 1, rect.Y + 1, Math.Max(0, rect.Width - 2), Math.Max(0, rect.Height - 2));
        }

        if (content.Width >= 2 && content.Height >= 2)
        {
            RenderDirectoryContent(dc, directory, content, depth, pixelsPerDip);
        }
    }

    private void RenderFile(DrawingContext dc, ScanNode file, Rect rect, double pixelsPerDip)
    {
        _rectCount++;
        _hits.Add(new HitRect(rect, file));
        var (fill, border) = FileStyles[file.Category];
        dc.DrawRectangle(fill, rect.Width > 3 && rect.Height > 3 ? border : null, rect);

        if (rect.Width > 80 && rect.Height > 34)
        {
            var labelArea = new Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4);
            DrawLabel(dc, $"{file.Name}\n{ByteSize.Format(file.Size)}", labelArea, DarkText, 11, pixelsPerDip);
        }
    }

    private static void DrawLabel(DrawingContext dc, string text, Rect area, Brush brush, double size, double pixelsPerDip)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("pt-BR"), FlowDirection.LeftToRight,
            LabelFace, size, brush, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, area.Width - 6),
            MaxTextHeight = Math.Max(1, area.Height - 1),
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.PushClip(new RectangleGeometry(area));
        dc.DrawText(formatted, new Point(area.X + 4, area.Y + 1));
        dc.Pop();
    }

    private HitRect? HitTest(Point p)
    {
        // A lista está em pré-ordem: o último retângulo que contém o ponto é o mais profundo.
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Rect.Contains(p))
            {
                return _hits[i];
            }
        }

        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this));
        if (ReferenceEquals(hit?.Node, HoveredNode))
        {
            return;
        }

        HoveredNode = hit?.Node;
        _highlight = hit?.Rect;
        if (hit is { } h)
        {
            var node = h.Node;
            _tooltipText.Text = $"{node.Name}\n{ByteSize.Format(node.Size)} · {ByteSize.FormatPercent(node.FractionOfParent)} da pasta-pai" +
                                (node.IsDirectory ? $" · {ByteSize.FormatCount(node.FileCount)} arquivos" : $" · {FileCategoryInfo.DisplayName(node.Category)}") +
                                $"\n{node.FullPath}";
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoveredNode = null;
        _highlight = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Focus();
        var hit = HitTest(e.GetPosition(this));
        if (hit is null || RootNode is null)
        {
            return;
        }

        // Entra na pasta mais profunda sob o cursor (para arquivos, na pasta que os contém).
        ScanNode? target = hit.Value.Node.IsDirectory ? hit.Value.Node : hit.Value.Node.Parent;
        if (target is not null && !ReferenceEquals(target, RootNode) && NavigateCommand?.CanExecute(target) == true)
        {
            NavigateCommand.Execute(target);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        ContextNode = HitTest(e.GetPosition(this))?.Node;
    }

    private static Dictionary<FileCategory, (Brush, Pen)> BuildFileStyles()
    {
        var styles = new Dictionary<FileCategory, (Brush, Pen)>();
        foreach (var category in FileCategoryInfo.All)
        {
            var color = (Color)ColorConverter.ConvertFromString(FileCategoryInfo.ColorHex(category));
            // Efeito "almofada" simples: mais claro no canto superior esquerdo, mais escuro no inferior direito.
            var fill = new LinearGradientBrush(Blend(color, Colors.White, 0.35), Blend(color, Colors.Black, 0.25), 45);
            var pen = new Pen(new SolidColorBrush(Blend(color, Colors.Black, 0.55)), 0.6);
            styles[category] = (Frozen(fill), Frozen(pen));
        }

        return styles;
    }

    private static Color Blend(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount),
        (byte)(a.B + (b.B - a.B) * amount));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private readonly record struct HitRect(Rect Rect, ScanNode Node);
}
