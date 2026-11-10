using Headroom.Core.Treemap;

namespace Headroom.Tests.Treemap;

public class SquarifiedLayoutTests
{
    private static readonly LayoutRect Bounds = new(10, 20, 600, 400);

    [Fact]
    public void Compute_AreasAreProportionalAndFillBounds()
    {
        double[] sizes = [60, 30, 20, 10, 5, 3, 2, 1];
        var rects = SquarifiedLayout.Compute(sizes, Bounds);

        double total = sizes.Sum();
        for (int i = 0; i < sizes.Length; i++)
        {
            Assert.Equal(Bounds.Area * sizes[i] / total, rects[i].Area, 6);
        }

        Assert.Equal(Bounds.Area, rects.Sum(r => r.Area), 6);
    }

    [Fact]
    public void Compute_RectanglesStayInsideBoundsAndDoNotOverlap()
    {
        var random = new Random(42);
        double[] sizes = Enumerable.Range(0, 60).Select(_ => random.NextDouble() * 1000 + 1)
            .OrderByDescending(x => x).ToArray();
        var rects = SquarifiedLayout.Compute(sizes, Bounds);

        const double eps = 1e-6;
        foreach (var r in rects)
        {
            Assert.True(r.X >= Bounds.X - eps && r.Y >= Bounds.Y - eps);
            Assert.True(r.Right <= Bounds.Right + eps && r.Bottom <= Bounds.Bottom + eps);
        }

        for (int i = 0; i < rects.Length; i++)
        {
            for (int j = i + 1; j < rects.Length; j++)
            {
                double overlapW = Math.Min(rects[i].Right, rects[j].Right) - Math.Max(rects[i].X, rects[j].X);
                double overlapH = Math.Min(rects[i].Bottom, rects[j].Bottom) - Math.Max(rects[i].Y, rects[j].Y);
                Assert.False(overlapW > eps && overlapH > eps, $"Retângulos {i} e {j} se sobrepõem");
            }
        }
    }

    [Fact]
    public void Compute_ProducesReasonableAspectRatios()
    {
        double[] sizes = Enumerable.Repeat(1.0, 16).ToArray();
        var rects = SquarifiedLayout.Compute(sizes, new LayoutRect(0, 0, 400, 400));

        foreach (var r in rects)
        {
            double ratio = Math.Max(r.Width / r.Height, r.Height / r.Width);
            Assert.True(ratio < 2.0, $"Proporção ruim: {ratio}");
        }
    }

    [Fact]
    public void Compute_HandlesZeroSizesAndEmptyInput()
    {
        Assert.Empty(SquarifiedLayout.Compute(Array.Empty<double>(), Bounds));

        var rects = SquarifiedLayout.Compute([10, 0, 0], Bounds);
        Assert.Equal(Bounds.Area, rects[0].Area, 6);
        Assert.Equal(0, rects[1].Area);
        Assert.Equal(0, rects[2].Area);

        var allZero = SquarifiedLayout.Compute([0, 0], Bounds);
        Assert.All(allZero, r => Assert.Equal(0, r.Area));
    }

    [Fact]
    public void Compute_SingleItemFillsEverything()
    {
        var rects = SquarifiedLayout.Compute([123], Bounds);
        Assert.Equal(Bounds, rects[0]);
    }
}
