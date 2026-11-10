namespace Headroom.Core.Treemap;

/// <summary>Retângulo em coordenadas de tela (double).</summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double px, double py) => px >= X && px < Right && py >= Y && py < Bottom;

    public LayoutRect Deflate(double amount)
    {
        double w = Math.Max(0, Width - 2 * amount);
        double h = Math.Max(0, Height - 2 * amount);
        return new LayoutRect(X + amount, Y + amount, w, h);
    }
}

/// <summary>
/// Algoritmo "squarified treemap" (Bruls, Huizing e van Wijk, 2000), implementado à mão.
/// Recebe tamanhos em ordem decrescente e devolve um retângulo por item, na mesma ordem,
/// preenchendo a área disponível com retângulos o mais próximos possível de quadrados.
/// </summary>
public static class SquarifiedLayout
{
    public static LayoutRect[] Compute(IReadOnlyList<double> sizes, LayoutRect bounds)
    {
        var result = new LayoutRect[sizes.Count];
        if (sizes.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return result;
        }

        double total = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            total += Math.Max(0, sizes[i]);
        }

        if (total <= 0)
        {
            return result;
        }

        // Converte tamanhos em áreas proporcionais à área disponível.
        double scale = bounds.Area / total;
        var areas = new double[sizes.Count];
        for (int i = 0; i < sizes.Count; i++)
        {
            areas[i] = Math.Max(0, sizes[i]) * scale;
        }

        double x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height;
        int start = 0;

        while (start < areas.Length)
        {
            // Itens com área zero recebem retângulos vazios.
            if (areas[start] <= 0)
            {
                result[start] = new LayoutRect(x, y, 0, 0);
                start++;
                continue;
            }

            double side = Math.Min(width, height);
            if (side <= 0)
            {
                for (int i = start; i < areas.Length; i++)
                {
                    result[i] = new LayoutRect(x, y, 0, 0);
                }

                break;
            }

            // Cresce a linha enquanto a pior proporção não piorar.
            int end = start;
            double rowSum = areas[start];
            double rowMin = areas[start];
            double rowMax = areas[start];
            double currentWorst = Worst(rowSum, rowMin, rowMax, side);

            while (end + 1 < areas.Length && areas[end + 1] > 0)
            {
                double next = areas[end + 1];
                double newSum = rowSum + next;
                double newMin = Math.Min(rowMin, next);
                double newMax = Math.Max(rowMax, next);
                double newWorst = Worst(newSum, newMin, newMax, side);
                if (newWorst > currentWorst)
                {
                    break;
                }

                end++;
                rowSum = newSum;
                rowMin = newMin;
                rowMax = newMax;
                currentWorst = newWorst;
            }

            // Posiciona a linha ao longo do lado menor.
            if (width >= height)
            {
                // Coluna vertical à esquerda.
                double columnWidth = rowSum / height;
                double cy = y;
                for (int i = start; i <= end; i++)
                {
                    double h = areas[i] / columnWidth;
                    result[i] = new LayoutRect(x, cy, columnWidth, h);
                    cy += h;
                }

                x += columnWidth;
                width = Math.Max(0, width - columnWidth);
            }
            else
            {
                // Linha horizontal no topo.
                double rowHeight = rowSum / width;
                double cx = x;
                for (int i = start; i <= end; i++)
                {
                    double w = areas[i] / rowHeight;
                    result[i] = new LayoutRect(cx, y, w, rowHeight);
                    cx += w;
                }

                y += rowHeight;
                height = Math.Max(0, height - rowHeight);
            }

            start = end + 1;
        }

        return result;
    }

    /// <summary>Pior razão de aspecto de uma linha com soma s, mínimo e máximo dados, no lado w.</summary>
    private static double Worst(double sum, double min, double max, double side)
    {
        double side2 = side * side;
        double sum2 = sum * sum;
        return Math.Max(side2 * max / sum2, sum2 / (side2 * min));
    }
}
