using Headroom.Core.Scanning;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Headroom.App.Helpers;

/// <summary>Cores dos gráficos (LiveCharts2) coerentes com o tema claro/escuro.</summary>
public static class ChartPalette
{
    public static readonly SKColor Accent = SKColor.Parse("#0F6CBD");
    public static readonly SKColor AccentLight = SKColor.Parse("#4CC2FF");

    public static SKColor Text(bool dark) => dark ? SKColor.Parse("#E6E8EB") : SKColor.Parse("#1B1F24");

    public static SKColor MutedText(bool dark) => dark ? SKColor.Parse("#A3ABB5") : SKColor.Parse("#5B6470");

    public static SKColor Grid(bool dark) => dark ? new SKColor(255, 255, 255, 28) : new SKColor(0, 0, 0, 22);

    public static SKColor TooltipBackground(bool dark) => dark ? SKColor.Parse("#2B2F35") : SKColor.Parse("#FFFFFF");

    public static SKColor Category(FileCategory category) => SKColor.Parse(FileCategoryInfo.ColorHex(category));

    public static SolidColorPaint Paint(SKColor color) => new(color);

    public static SolidColorPaint Stroke(SKColor color, float width = 1) => new(color) { StrokeThickness = width };
}
