using HiveDB.Storage;
using HiveDB.Tree;

namespace HiveDB.Tests;

public class PageManagerTests : IDisposable
{
    private readonly string _filePath;
    private readonly PageManager _pages;

    public PageManagerTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"hivedb_test_{Guid.NewGuid()}.dat");
        var file = new BinaryFileHandle(_filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var cache = new PageCache();
        _pages = new PageManager(file, cache);

        // Initialize with header (page 0) + root key (page 1)
        var rootBuffer = new byte[FileHeader.PageSize];
        new KeyPage { PageNumber = 1, KeyName = "" }.Write(rootBuffer);
        _pages.WritePage(1, rootBuffer);

        var header = FileHeader.CreateNew(rootKeyPage: 1);
        var headerBuffer = new byte[FileHeader.PageSize];
        header.Write(headerBuffer);
        file.WritePage(0, headerBuffer);

        file.SetLength(2 * FileHeader.PageSize);
    }

    public void Dispose()
    {
        _pages.Dispose();
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    [Fact]
    public void AllocatePage_ReturnsSequentialNumbers()
    {
        int p1 = _pages.AllocatePage(PageType.Key);
        int p2 = _pages.AllocatePage(PageType.Key);
        Assert.NotEqual(p1, p2);
    }

    [Fact]
    public void FreePage_ReturnsToFreeList()
    {
        int page = _pages.AllocatePage(PageType.Key);
        int freeBefore = _pages.FreePageCount;

        _pages.FreePage(page);
        Assert.Equal(freeBefore + 1, _pages.FreePageCount);
    }

    [Fact]
    public void AllocatePage_ReusesFreePages()
    {
        int page = _pages.AllocatePage(PageType.Key);
        _pages.FreePage(page);

        int reused = _pages.AllocatePage(PageType.Key);
        Assert.Equal(page, reused);
        Assert.Equal(0, _pages.FreePageCount);
    }

    [Fact]
    public void FreeOverflowChain_ReturnsAllPages()
    {
        var data = new byte[10000];
        new Random(42).NextBytes(data);
        int firstPage = _pages.WriteOverflowChain(data, 0, data.Length);

        int freeBefore = _pages.FreePageCount;
        _pages.FreeOverflowChain(firstPage);
        Assert.True(_pages.FreePageCount > freeBefore);
    }

    [Fact]
    public void ReadOverflowChain_RoundTrip()
    {
        var original = new byte[10000];
        new Random(42).NextBytes(original);
        int firstPage = _pages.WriteOverflowChain(original, 0, original.Length);

        byte[] result = _pages.ReadOverflowChain(firstPage, original.Length);
        Assert.Equal(original, result);
    }

    [Fact]
    public void ReadHeader_ReturnsValidHeader()
    {
        var header = _pages.ReadHeader();
        Assert.Equal(FileHeader.MagicValue, header.Magic);
        Assert.Equal(FileHeader.CurrentVersion, header.Version);
    }
}
