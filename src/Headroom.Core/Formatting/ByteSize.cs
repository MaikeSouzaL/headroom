using System.Globalization;

namespace Headroom.Core.Formatting;

/// <summary>
/// Formatação de tamanhos em bytes no padrão brasileiro (vírgula decimal, base 1024).
/// </summary>
public static class ByteSize
{
    public const long KB = 1024;
    public const long MB = KB * 1024;
    public const long GB = MB * 1024;
    public const long TB = GB * 1024;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formata bytes de forma legível: "512 B", "1,52 KB", "12,3 GB", "123 GB".</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Format(-bytes);
        }

        if (bytes < KB)
        {
            return bytes.ToString(PtBr) + " B";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value < 10 ? "0.##" : value < 100 ? "0.#" : "0";
        return value.ToString(format, PtBr) + " " + Units[unit];
    }

    /// <summary>Formata sempre em GB com duas casas: "12,34 GB".</summary>
    public static string FormatGb(long bytes) => (bytes / (double)GB).ToString("N2", PtBr) + " GB";

    /// <summary>Formata um número inteiro com separador de milhar brasileiro: 1.234.567.</summary>
    public static string FormatCount(long count) => count.ToString("N0", PtBr);

    /// <summary>Formata uma porcentagem (0..1) como "12,5%".</summary>
    public static string FormatPercent(double fraction) => (fraction * 100).ToString("0.#", PtBr) + "%";

    /// <summary>Formata uma duração: "1 min 05 s", "12,4 s", "2 h 03 min".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
        {
            return duration.TotalSeconds.ToString("0.0", PtBr) + " s";
        }

        if (duration.TotalHours < 1)
        {
            return $"{(int)duration.TotalMinutes} min {duration.Seconds:00} s";
        }

        return $"{(int)duration.TotalHours} h {duration.Minutes:00} min";
    }

    /// <summary>
    /// Interpreta textos como "500 MB", "1,5 GB", "2048" (bytes) ou "10k".
    /// </summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim().ToUpperInvariant().Replace(" ", string.Empty);
        long multiplier = 1;
        (string suffix, long mult)[] suffixes =
        [
            ("TB", TB), ("GB", GB), ("MB", MB), ("KB", KB), ("T", TB), ("G", GB), ("M", MB), ("K", KB), ("B", 1)
        ];

        foreach (var (suffix, mult) in suffixes)
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
            {
                multiplier = mult;
                s = s[..^suffix.Length];
                break;
            }
        }

        s = s.Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0)
        {
            return false;
        }

        bytes = (long)Math.Round(value * multiplier);
        return true;
    }
}
