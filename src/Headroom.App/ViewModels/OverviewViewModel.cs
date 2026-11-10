using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Converters;
using Headroom.App.Helpers;
using Headroom.App.Services;
using Headroom.Core.Formatting;
using Headroom.Core.Reports;
using Headroom.Core.Scanning;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace Headroom.App.ViewModels;

/// <summary>Linha da legenda de categorias.</summary>
public sealed record CategoryLegendItem(string Name, Brush Color, string SizeText, string PercentText, string CountText);

/// <summary>Visão geral: cards, rosca por categoria, barras das 15 maiores pastas e explicações.</summary>
public sealed partial class OverviewViewModel : FileActionsViewModel, IPageActivated
{
    private bool _dirty = true;

    public OverviewViewModel(AppServices services) : base(services)
    {
        services.Session.ResultChanged += (_, _) => _dirty = true;
        services.Theme.ThemeChanged += (_, _) => { _dirty = true; Refresh(); };
        Refresh();
    }

    [ObservableProperty] private string _rootPath = string.Empty;
    [ObservableProperty] private string _totalText = "—";
    [ObservableProperty] private string _freeText = "—";
    [ObservableProperty] private string _freeCaption = string.Empty;
    [ObservableProperty] private string _filesText = "—";
    [ObservableProperty] private string _foldersText = string.Empty;
    [ObservableProperty] private string _recoverableText = "—";
    [ObservableProperty] private string _scanInfoText = string.Empty;
    [ObservableProperty] private string _differenceText = string.Empty;
    [ObservableProperty] private string _devArtifactsText = string.Empty;
    [ObservableProperty] private bool _hasDevArtifacts;
    [ObservableProperty] private bool _hasRecoverable;
    [ObservableProperty] private ISeries[] _categorySeries = Array.Empty<ISeries>();
    [ObservableProperty] private ISeries[] _topFolderSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ICartesianAxis[] _topFolderYAxes = Array.Empty<ICartesianAxis>();
    [ObservableProperty] private ICartesianAxis[] _topFolderXAxes = Array.Empty<ICartesianAxis>();
    [ObservableProperty] private SolidColorPaint _tooltipTextPaint = ChartPalette.Paint(ChartPalette.Text(false));
    [ObservableProperty] private SolidColorPaint _tooltipBackgroundPaint = ChartPalette.Paint(ChartPalette.TooltipBackground(false));
    [ObservableProperty] private double _topFoldersChartHeight = 420;

    public ObservableCollection<CategoryLegendItem> Legend { get; } = new();
    public ObservableCollection<RecoverableItem> Recoverable { get; } = new();

    public void OnActivated()
    {
        if (_dirty)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var result = Result;
        if (result is null)
        {
            return;
        }

        _dirty = false;
        bool dark = Services.Theme.IsDark;
        TooltipTextPaint = ChartPalette.Paint(ChartPalette.Text(dark));
        TooltipBackgroundPaint = ChartPalette.Paint(ChartPalette.TooltipBackground(dark));

        RootPath = result.RootPath;
        TotalText = ByteSize.Format(result.TotalBytes);
        FilesText = ByteSize.FormatCount(result.FileCount);
        FoldersText = $"{ByteSize.FormatCount(result.DirectoryCount)} pastas";
        if (result.Drive is { } drive)
        {
            FreeText = ByteSize.Format(drive.FreeBytes);
            FreeCaption = $"de {ByteSize.Format(drive.TotalBytes)} ({ByteSize.FormatPercent(drive.FreeBytes / (double)Math.Max(1, drive.TotalBytes))})";
        }
        else
        {
            var info = Core.Drives.DriveService.GetDrive(result.RootPath);
            FreeText = info is null ? "—" : ByteSize.Format(info.FreeBytes);
            FreeCaption = info is null ? string.Empty : $"livres em {info.Letter}";
        }

        var recoverable = RecoverableSpaceEstimator.Estimate(result);
        Recoverable.Clear();
        foreach (var item in recoverable.Take(12))
        {
            Recoverable.Add(item);
        }

        RecoverableText = ByteSize.Format(recoverable.Sum(r => r.Bytes));
        HasRecoverable = recoverable.Count > 0;
        ScanInfoText = $"Scan de {result.RootPath} concluído em {ByteSize.FormatDuration(result.Elapsed)} " +
                       $"({result.StartedAt:dd/MM/yyyy HH:mm}). Encontrado: {ByteSize.Format(result.TotalBytes)} em " +
                       $"{ByteSize.FormatCount(result.FileCount)} arquivos." +
                       (result.CloudOnlySkipped > 0 ? $" {ByteSize.FormatCount(result.CloudOnlySkipped)} arquivo(s) só na nuvem ignorados." : string.Empty) +
                       (result.LinksSkipped > 0 ? $" {ByteSize.FormatCount(result.LinksSkipped)} junction(s)/link(s) não seguidos." : string.Empty);
        DifferenceText = ScanExplanations.DifferenceText(result);

        HasDevArtifacts = result.DevArtifacts.Count > 0;
        DevArtifactsText = HasDevArtifacts
            ? $"{ByteSize.FormatCount(result.DevArtifacts.Count)} pastas de artefatos de desenvolvimento (node_modules, bin/obj, .venv, __pycache__, .gradle, Pods) somando {ByteSize.Format(result.DevArtifactsBytes)}."
            : "Nenhuma pasta de artefatos de desenvolvimento encontrada.";

        BuildCategoryChart(result);
        BuildTopFoldersChart(result, dark);
    }

    private void BuildCategoryChart(ScanResult result)
    {
        var categories = result.Categories.Where(c => c.TotalBytes > 0).ToList();
        long total = Math.Max(1, categories.Sum(c => c.TotalBytes));

        CategorySeries = categories.Select(c => (ISeries)new PieSeries<double>
        {
            Values = new[] { (double)c.TotalBytes },
            Name = c.DisplayName,
            InnerRadius = 70,
            Fill = ChartPalette.Paint(ChartPalette.Category(c.Category)),
            HoverPushout = 6,
            ToolTipLabelFormatter = point => $"{ByteSize.Format((long)point.Coordinate.PrimaryValue)} ({ByteSize.FormatPercent(point.Coordinate.PrimaryValue / total)})",
        }).ToArray();

        Legend.Clear();
        foreach (var c in categories)
        {
            Legend.Add(new CategoryLegendItem(c.DisplayName, CategoryBrushConverter.GetBrush(c.Category),
                ByteSize.Format(c.TotalBytes), ByteSize.FormatPercent(c.TotalBytes / (double)total),
                $"{ByteSize.FormatCount(c.FileCount)} arquivos"));
        }
    }

    private void BuildTopFoldersChart(ScanResult result, bool dark)
    {
        // As 15 maiores pastas de primeiro nível; a série de barras desenha de baixo para cima.
        var top = result.Root.Children.Where(n => n.IsDirectory).Take(15).Reverse().ToList();
        var names = top.Select(n => Shorten(n.Name, 28)).ToArray();

        TopFoldersChartHeight = Math.Max(160, top.Count * 30 + 40);
        TopFolderSeries = new ISeries[]
        {
            new RowSeries<double>
            {
                Values = top.Select(n => (double)n.Size).ToArray(),
                Name = "Tamanho",
                Fill = ChartPalette.Paint(dark ? ChartPalette.AccentLight : ChartPalette.Accent),
                MaxBarWidth = 22,
                Rx = 3,
                Ry = 3,
                XToolTipLabelFormatter = point => ByteSize.Format((long)point.Coordinate.PrimaryValue),
                DataLabelsPaint = ChartPalette.Paint(ChartPalette.Text(dark)),
                DataLabelsSize = 12,
                DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.End,
                DataLabelsFormatter = point => ByteSize.Format((long)point.Coordinate.PrimaryValue),
            },
        };

        TopFolderYAxes = new ICartesianAxis[]
        {
            new Axis
            {
                Labels = names,
                LabelsPaint = ChartPalette.Paint(ChartPalette.Text(dark)),
                TextSize = 12,
                MinStep = 1,
                ForceStepToMin = true,
                SeparatorsPaint = null,
            },
        };

        TopFolderXAxes = new ICartesianAxis[]
        {
            new Axis
            {
                Labeler = value => value < 0 ? string.Empty : ByteSize.Format((long)value),
                LabelsPaint = ChartPalette.Paint(ChartPalette.MutedText(dark)),
                SeparatorsPaint = ChartPalette.Stroke(ChartPalette.Grid(dark)),
                TextSize = 11,
                MinLimit = 0,

                // Folga à direita para o rótulo da maior barra não ser cortado.
                MaxLimit = top.Count == 0 ? null : top.Max(n => (double)n.Size) * 1.18,
            },
        };
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (Result is not { } result)
        {
            return;
        }

        string? path = Services.Dialogs.SaveFile("Exportar relatório HTML", "Página HTML (*.html)|*.html",
            $"Headroom-{SafeName(result.RootPath)}-{DateTime.Now:yyyyMMdd-HHmm}.html");
        if (path is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => HtmlReportExporter.Export(result, path));
            Services.Shell.OpenUrlOrFile(path);
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ErrorAsync("Não foi possível exportar", ex);
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Result is not { } result)
        {
            return;
        }

        string? path = Services.Dialogs.SaveFile("Exportar CSV", "CSV separado por ponto e vírgula (*.csv)|*.csv",
            $"Headroom-{SafeName(result.RootPath)}-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        if (path is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => CsvReportExporter.Export(result, path));
            await Services.Dialogs.InfoAsync("Exportado", $"Relatório salvo em:\n{path}");
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ErrorAsync("Não foi possível exportar", ex);
        }
    }

    [RelayCommand]
    private void GoToCleanup() => Services.Navigator.Navigate(PageKind.Cleanup);

    [RelayCommand]
    private void GoToTreemap() => Services.Navigator.Navigate(PageKind.Treemap);

    private static string SafeName(string path)
    {
        var chars = path.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars).Trim('_');
    }
}
