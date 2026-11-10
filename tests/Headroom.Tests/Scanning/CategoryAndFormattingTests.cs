using Headroom.Core.Formatting;
using Headroom.Core.Scanning;

namespace Headroom.Tests.Scanning;

public class CategoryAndFormattingTests
{
    [Theory]
    [InlineData("filme.mkv", FileCategory.Video)]
    [InlineData("FOTO.HEIC", FileCategory.Image)]
    [InlineData("musica.flac", FileCategory.Audio)]
    [InlineData("relatorio.pdf", FileCategory.Document)]
    [InlineData("backup.7z", FileCategory.Archive)]
    [InlineData("setup.msi", FileCategory.Installer)]
    [InlineData("windows.iso", FileCategory.Installer)]
    [InlineData("Program.cs", FileCategory.Code)]
    [InlineData("pagefile.sys", FileCategory.System)]
    [InlineData(".gitignore", FileCategory.Other)]
    [InlineData("sem_extensao", FileCategory.Other)]
    [InlineData("arquivo.desconhecida", FileCategory.Other)]
    public void Classify_ByExtension(string name, FileCategory expected)
    {
        Assert.Equal(expected, CategoryClassifier.Classify(name));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    [InlineData(1288490189L, "1,2 GB")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 GB")]
    public void Format_UsesBrazilianDecimalComma(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Theory]
    [InlineData("500 MB", 500L * 1024 * 1024)]
    [InlineData("1,5 GB", 1610612736L)]
    [InlineData("2048", 2048L)]
    [InlineData("10k", 10240L)]
    public void TryParse_AcceptsCommonFormats(string text, long expected)
    {
        Assert.True(ByteSize.TryParse(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void TryParse_RejectsInvalidText()
    {
        Assert.False(ByteSize.TryParse("abc", out _));
        Assert.False(ByteSize.TryParse("", out _));
        Assert.False(ByteSize.TryParse("-5 MB", out _));
    }

    [Fact]
    public void FormatDuration_IsReadable()
    {
        Assert.Equal("12,5 s", ByteSize.FormatDuration(TimeSpan.FromSeconds(12.5)));
        Assert.Equal("1 min 05 s", ByteSize.FormatDuration(TimeSpan.FromSeconds(65)));
        Assert.Equal("2 h 03 min", ByteSize.FormatDuration(TimeSpan.FromMinutes(123)));
    }
}
