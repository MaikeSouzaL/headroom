using Headroom.Core.Duplicates;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Duplicates;

public class DuplicateFinderTests
{
    private static IEnumerable<FileCandidate> Candidates(string root) =>
        new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => new FileCandidate(f.FullName, f.Length, f.LastWriteTimeUtc));

    [Fact]
    public async Task Find_GroupsIdenticalFilesOnly()
    {
        using var temp = new TempDirectory();
        var content = new byte[200_000];
        new Random(1).NextBytes(content);

        temp.CreateFileWithContent(@"a\original.bin", content);
        temp.CreateFileWithContent(@"b\copia.bin", content);
        temp.CreateFileWithContent(@"c\copia2.bin", content);

        // Mesmo tamanho e mesmos primeiros 64 KB, mas final diferente.
        var almost = (byte[])content.Clone();
        almost[^1] ^= 0xFF;
        temp.CreateFileWithContent(@"d\quase.bin", almost);

        // Mesmo tamanho, conteúdo totalmente diferente.
        temp.CreateFile(@"e\outro.bin", content.Length, seed: 99);

        var groups = await new DuplicateFinder().FindAsync(Candidates(temp.Path), new DuplicateOptions { MinimumSize = 1 });

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Files.Count);
        Assert.Equal(content.Length * 2L, group.WastedBytes);
        Assert.DoesNotContain(group.Files, f => f.Name is "quase.bin" or "outro.bin");
    }

    [Theory]
    [InlineData(500_000)] // passa pelas etapas de início, fim e hash completo
    [InlineData(100_000)] // entre 64 KB e 128 KB: sem etapa do fim, vai direto ao hash completo
    public async Task Find_SeparatesFilesThatDifferOnlyInTheMiddle(int size)
    {
        using var temp = new TempDirectory();
        var content = new byte[size];
        new Random(11).NextBytes(content);
        temp.CreateFileWithContent("um.bin", content);
        temp.CreateFileWithContent("dois.bin", content);

        // Início e fim iguais; só um byte do meio muda.
        var middle = (byte[])content.Clone();
        middle[size / 2] ^= 0xFF;
        temp.CreateFileWithContent("meio.bin", middle);

        var groups = await new DuplicateFinder().FindAsync(Candidates(temp.Path), new DuplicateOptions { MinimumSize = 1 });

        var group = Assert.Single(groups);
        Assert.Equal(new[] { "dois.bin", "um.bin" }, group.Files.Select(f => f.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Find_HandlesSmallFilesSmallerThanPartialBlock()
    {
        using var temp = new TempDirectory();
        var small = new byte[1000];
        new Random(5).NextBytes(small);
        temp.CreateFileWithContent("x1.dat", small);
        temp.CreateFileWithContent("x2.dat", small);
        temp.CreateFile("x3.dat", 1000, seed: 7);

        var groups = await new DuplicateFinder().FindAsync(Candidates(temp.Path), new DuplicateOptions { MinimumSize = 0 });

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
    }

    [Fact]
    public async Task Find_IgnoresHardLinksToTheSameFile()
    {
        using var temp = new TempDirectory();
        string original = temp.CreateFile("original.bin", 100_000, seed: 3);
        TempDirectory.CreateHardLink(temp.Combine("hardlink.bin"), original);

        var groups = await new DuplicateFinder().FindAsync(Candidates(temp.Path), new DuplicateOptions { MinimumSize = 1 });

        Assert.Empty(groups);
    }

    [Fact]
    public async Task Find_RespectsMinimumSize()
    {
        using var temp = new TempDirectory();
        var content = new byte[5000];
        temp.CreateFileWithContent("a.bin", content);
        temp.CreateFileWithContent("b.bin", content);

        var groups = await new DuplicateFinder().FindAsync(Candidates(temp.Path), new DuplicateOptions { MinimumSize = 10_000 });

        Assert.Empty(groups);
    }

    [Fact]
    public async Task SuggestedKeep_IsTheOldestFile()
    {
        using var temp = new TempDirectory();
        var content = new byte[3000];
        new Random(11).NextBytes(content);
        string older = temp.CreateFileWithContent("antigo.bin", content);
        string newer = temp.CreateFileWithContent("novo.bin", content);
        File.SetCreationTimeUtc(older, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetCreationTimeUtc(newer, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var groups = await new DuplicateFinder().FindAsync(Candidates(temp.Path), new DuplicateOptions { MinimumSize = 1 });

        Assert.Equal("antigo.bin", Assert.Single(groups).SuggestedKeep.Name);
    }
}
