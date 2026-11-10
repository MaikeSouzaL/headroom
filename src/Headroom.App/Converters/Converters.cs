using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;

namespace Headroom.App.Converters;

/// <summary>long (bytes) → "12,3 GB".</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long l => ByteSize.Format(l),
        int i => ByteSize.Format(i),
        double d => ByteSize.Format((long)d),
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>long → "1.234.567".</summary>
public sealed class CountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long l => ByteSize.FormatCount(l),
        int i => ByteSize.FormatCount(i),
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>double (0..1) → "12,5%".</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? ByteSize.FormatPercent(d) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>DateTime → "15/10/2026 14:32" (ou só a data com parâmetro "d").</summary>
public sealed class DateConverter : IValueConverter
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime dt || dt == DateTime.MinValue || dt == DateTime.MaxValue)
        {
            return string.Empty;
        }

        var local = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
        return parameter as string == "d" ? local.ToString("dd/MM/yyyy", PtBr) : local.ToString("dd/MM/yyyy HH:mm", PtBr);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → Visibility (parâmetro "inverse" inverte).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (parameter as string == "inverse")
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>null/vazio → Collapsed (parâmetro "inverse" inverte).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };

        if (parameter as string == "inverse")
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → !bool.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>FileCategory → pincel da cor da categoria.</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    private static readonly Dictionary<FileCategory, SolidColorBrush> Cache = new();

    public static SolidColorBrush GetBrush(FileCategory category)
    {
        if (!Cache.TryGetValue(category, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(FileCategoryInfo.ColorHex(category)));
            brush.Freeze();
            Cache[category] = brush;
        }

        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FileCategory c ? GetBrush(c) : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>FileCategory → "Vídeos".</summary>
public sealed class CategoryNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FileCategory c ? FileCategoryInfo.DisplayName(c) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>int (profundidade) → margem esquerda para indentação na árvore.</summary>
public sealed class IndentConverter : IValueConverter
{
    public double Step { get; set; } = 18;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int depth ? new Thickness(depth * Step, 0, 0, 0) : new Thickness(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Enum ↔ bool para RadioButtons (parâmetro = nome do valor).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string p ? Enum.Parse(targetType, p) : Binding.DoNothing;
}
