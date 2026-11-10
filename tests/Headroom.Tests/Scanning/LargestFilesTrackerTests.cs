using Headroom.Core.Scanning;

namespace Headroom.Tests.Scanning;

public class LargestFilesTrackerTests
{
    [Fact]
    public void Offer_KeepsTheLargestItems()
    {
        var root = (DirectoryNode)ScanNode.CreateRoot(@"C:\", DateTime.UtcNow);
        var tracker = new LargestFilesTracker(3);
        foreach (long size in new long[] { 5, 1, 9, 3, 7, 8, 2 })
        {
            tracker.Offer(NewFile(root, size));
        }

        Assert.Equal(new long[] { 9, 8, 7 }, tracker.ToSortedList().Select(n => n.Size));
    }

    [Fact]
    public void Offer_IsThreadSafe()
    {
        var root = (DirectoryNode)ScanNode.CreateRoot(@"C:\", DateTime.UtcNow);
        var tracker = new LargestFilesTracker(100);
        Parallel.For(0, 100_000, i => tracker.Offer(NewFile(root, i)));

        var list = tracker.ToSortedList();
        Assert.Equal(100, list.Count);
        Assert.Equal(99_999, list[0].Size);
        Assert.Equal(99_900, list[^1].Size);
    }

    [Fact]
    public void ToSortedList_WithFewerItemsThanCapacity()
    {
        var root = (DirectoryNode)ScanNode.CreateRoot(@"C:\", DateTime.UtcNow);
        var tracker = new LargestFilesTracker(10);
        tracker.Offer(NewFile(root, 1));
        tracker.Offer(NewFile(root, 2));

        Assert.Equal(new long[] { 2, 1 }, tracker.ToSortedList().Select(n => n.Size));
    }

    private static ScanNode NewFile(DirectoryNode parent, long size) =>
        ScanNode.CreateFile("f" + size, parent, size, DateTime.UtcNow.Ticks, FileCategory.Other);
}
