using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Helpers;
using Headroom.App.Services;
using Headroom.Core.Formatting;
using Headroom.Core.History;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Headroom.App.ViewModels;

public sealed record ScanHistoryRow(DateTime Date, string Root, string Total, string Files, string Free, string Duration);

public sealed record CleanupHistoryRow(DateTime Date, string Freed, string RecycleBin, int Rules, int Skipped, int Errors, string Origin, string? LogPath);

/// <summary>Histórico: evolução do espaço livre (gráfico de linha), scans e limpezas anteriores.</summary>
public sealed partial class HistoryViewModel : ObservableObject, IPageActivated
{
    private static readonly SKColor[] DriveColors =
    [
        SKColor.Parse("#0F6CBD"), SKColor.Parse("#37B24D"), SKColor.Parse("#F76707"), SKColor.Parse("#AE3EC9"), SKColor.Parse("#E03131"),
    ];

    private readonly AppServices _services;

    public HistoryViewModel(AppServices services)
    {
        _services = services;
        services.Theme.ThemeChanged += (_, _) => Refresh();
    }

    public ObservableCollection<ScanHistoryRow> Scans { get; } = new();
    public ObservableCollection<CleanupHistoryRow> Cleanups { get; } = new();

    [ObservableProperty] private ISeries[] _freeSpaceSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ICartesianAxis[] _xAxes = Array.Empty<ICartesianAxis>();
    [ObservableProperty] private ICartesianAxis[] _yAxes = Array.Empty<ICartesianAxis>();
    [ObservableProperty] private SolidColorPaint _legendPaint = ChartPalette.Paint(ChartPalette.Text(false));
    [ObservableProperty] private SolidColorPaint _tooltipTextPaint = ChartPalette.Paint(ChartPalette.Text(false));
    [ObservableProperty] private SolidColorPaint _tooltipBackgroundPaint = ChartPalette.Paint(ChartPalette.TooltipBackground(false));
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _hasData;

    public void OnActivated() => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        var data = _services.History.Load();
        bool dark = _services.Theme.IsDark;
        LegendPaint = ChartPalette.Paint(ChartPalette.Text(dark));
        TooltipTextPaint = ChartPalette.Paint(ChartPalette.Text(dark));
        TooltipBackgroundPaint = ChartPalette.Paint(ChartPalette.TooltipBackground(dark));

        var series = new List<ISeries>();
        int colorIndex = 0;
        foreach (var drive in data.FreeSpace.GroupBy(s => s.Drive).OrderBy(g => g.Key))
        {
            var color = DriveColors[colorIndex++ % DriveColors.Length];
            series.Add(new LineSeries<DateTimePoint>
            {
                Name = $"Livre em {drive.Key.TrimEnd('\\')}",
                Values = drive.OrderBy(s => s.Date).Select(s => new DateTimePoint(s.Date, s.FreeBytes / (double)ByteSize.GB)).ToArray(),
                Stroke = ChartPalette.Stroke(color, 2),
                Fill = null,
                GeometrySize = 6,
                GeometryStroke = ChartPalette.Stroke(color, 2),
                GeometryFill = ChartPalette.Paint(color),
                LineSmoothness = 0.2,
                YToolTipLabelFormatter = p => $"{p.Coordinate.PrimaryValue:0.0} GB livres",
            });
        }

        FreeSpaceSeries = series.ToArray();
        XAxes = new ICartesianAxis[]
        {
            new DateTimeAxis(TimeSpan.FromDays(1), d => d.ToString("dd/MM"))
            {
                LabelsPaint = ChartPalette.Paint(ChartPalette.MutedText(dark)),
                SeparatorsPaint = ChartPalette.Stroke(ChartPalette.Grid(dark)),
                TextSize = 11,
            },
        };
        YAxes = new ICartesianAxis[]
        {
            new Axis
            {
                Labeler = v => $"{v:0} GB",
                LabelsPaint = ChartPalette.Paint(ChartPalette.MutedText(dark)),
                SeparatorsPaint = ChartPalette.Stroke(ChartPalette.Grid(dark)),
                TextSize = 11,
                MinLimit = 0,
            },
        };

        Scans.Clear();
        foreach (var s in data.Scans.OrderByDescending(s => s.Date).Take(200))
        {
            Scans.Add(new ScanHistoryRow(s.Date, s.RootPath, ByteSize.Format(s.TotalBytes), ByteSize.FormatCount(s.FileCount),
                s.DriveFreeBytes is long free ? ByteSize.Format(free) : "—", ByteSize.FormatDuration(TimeSpan.FromSeconds(s.ElapsedSeconds))));
        }

        Cleanups.Clear();
        foreach (var c in data.Cleanups.OrderByDescending(c => c.Date).Take(200))
        {
            Cleanups.Add(new CleanupHistoryRow(c.Date, ByteSize.Format(c.BytesFreed), ByteSize.Format(c.BytesToRecycleBin), c.RulesRun,
                c.Skipped, c.Errors, c.Origin, c.LogPath));
        }

        HasData = data.FreeSpace.Count > 0;
        long totalFreed = data.Cleanups.Sum(c => c.BytesFreed);
        Summary = HasData
            ? $"{data.Scans.Count} scan(s) e {data.Cleanups.Count} limpeza(s) registrados. Total limpo até hoje: {ByteSize.Format(totalFreed)}."
            : "Ainda não há histórico. Ele é gravado a cada scan, limpeza e verificação agendada.";
    }

    [RelayCommand]
    private void RecordNow()
    {
        _services.History.RecordFreeSpace();
        Refresh();
    }

    [RelayCommand]
    private void OpenLog(CleanupHistoryRow? row)
    {
        if (row?.LogPath is { } path && File.Exists(path))
        {
            _services.Shell.OpenUrlOrFile(path);
        }
    }
}
