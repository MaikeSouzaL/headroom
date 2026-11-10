using Headroom.Core.Cleanup.Safety;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Cleanup;

public class DeletionGuardTests
{
    private static readonly DeletionGuard Guard = DeletionGuard.Default;
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Fact]
    public void Check_RequiresItemInsideAllowedRoots()
    {
        using var temp = new TempDirectory();
        var roots = new[] { new AllowedRoot(temp.Combine("cache")) };

        Assert.False(Guard.Check(temp.Combine("cache", "a.tmp"), roots).IsProtected);
        Assert.False(Guard.Check(temp.Combine("cache", "sub", "b.tmp"), roots).IsProtected);
        Assert.True(Guard.Check(temp.Combine("outro", "a.tmp"), roots).IsProtected);
        Assert.True(Guard.Check(temp.Combine("cache2", "a.tmp"), roots).IsProtected); // prefixo parecido não vale
        Assert.True(Guard.Check(temp.Combine("cache", "..", "fora.tmp"), roots).IsProtected);
    }

    [Fact]
    public void Check_RootItselfOnlyWhenExplicitlyAllowed()
    {
        using var temp = new TempDirectory();
        string root = temp.Combine("node_modules");

        Assert.True(Guard.Check(root, new[] { new AllowedRoot(root) }).IsProtected);
        Assert.False(Guard.Check(root, new[] { new AllowedRoot(root, AllowRootItself: true) }).IsProtected);
    }

    [Fact]
    public void Check_NoRuleCanEscapeTheBlacklist()
    {
        // Mesmo que uma regra mal escrita permita C:\ ou C:\Windows inteiro, a lista negra vence.
        var everything = new[] { new AllowedRoot(@"C:\", AllowRootItself: true), new AllowedRoot(Windows, true) };

        Assert.True(Guard.Check(Path.Combine(Windows, "System32", "kernel32.dll"), everything).IsProtected);
        Assert.True(Guard.Check(Path.Combine(Windows, "WinSxS", "x"), everything).IsProtected);
        Assert.True(Guard.Check(Path.Combine(Windows, "Installer", "x.msi"), everything).IsProtected);
        Assert.True(Guard.Check(@"C:\pagefile.sys", everything).IsProtected);
        Assert.True(Guard.Check(@"C:\", everything).IsProtected);
        Assert.True(Guard.Check(Windows, everything).IsProtected);
    }

    [Fact]
    public void Check_RefusesPathsThroughJunctions()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        outside.CreateFile("importante.txt", 10);
        TempDirectory.CreateJunction(root.Combine("link"), outside.Path);

        var roots = new[] { new AllowedRoot(root.Path) };
        var result = Guard.Check(root.Combine("link", "importante.txt"), roots);

        Assert.True(result.IsProtected);
        Assert.Contains("link", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_WithoutRootsUsesOnlyBlacklist()
    {
        Assert.False(Guard.Check(@"D:\Videos\filme.mkv").IsProtected);
        Assert.True(Guard.Check(Path.Combine(Windows, "System32", "cmd.exe")).IsProtected);
    }
}
