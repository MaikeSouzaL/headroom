using Headroom.Core.Scanning;

namespace Headroom.Tests.Scanning;

public class RecycleBinPathTests
{
    [Theory]
    [InlineData(@"E:\$RECYCLE.BIN\S-1-5-21-1-2-3-500\$RLB5KHL.so", true)]
    [InlineData(@"c:\$Recycle.Bin\S-1-5-18\$I1234.txt", true)]
    [InlineData(@"E:\$RECYCLE.BIN", false)]
    [InlineData(@"E:\Projetos\$Recycle.Bin\arquivo.txt", false)]
    [InlineData(@"E:\Leva_Mais\android\libreactnative.so", false)]
    public void Contains_OnlyMatchesTheRecycleBinAtTheDriveRoot(string path, bool expected) =>
        Assert.Equal(expected, RecycleBinPath.Contains(path));
}
