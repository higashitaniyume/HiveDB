using HiveDB.Storage;
using HiveDB.Tree;

namespace HiveDB.Tests;

public class TreeNavigatorTests : IDisposable
{
    private readonly string _filePath;
    private readonly PageManager _pages;
    private readonly TreeNavigator _navigator;

    public TreeNavigatorTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"hivedb_test_{Guid.NewGuid()}.dat");
        var file = new BinaryFileHandle(_filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var cache = new PageCache();
        _pages = new PageManager(file, cache);
        _navigator = new TreeNavigator(_pages);

        // Write root key at page 1, header at page 0
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
    public void ResolveKey_Root_ReturnsRootPage()
    {
        var key = _navigator.ResolveKey("", createMissing: false);
        Assert.NotNull(key);
        Assert.Equal("", key!.KeyName);
    }

    [Fact]
    public void ResolveKey_SingleLevel()
    {
        _navigator.ResolveKey("Software", createMissing: true);
        var key = _navigator.ResolveKey("Software", createMissing: false);
        Assert.NotNull(key);
        Assert.Equal("Software", key!.KeyName);
    }

    [Fact]
    public void ResolveKey_MultiLevelDeep()
    {
        _navigator.ResolveKey(@"Software\MyApp\Settings", createMissing: true);
        var key = _navigator.ResolveKey(@"Software\MyApp\Settings", createMissing: false);
        Assert.NotNull(key);
        Assert.Equal("Settings", key!.KeyName);
    }

    [Fact]
    public void ResolveKey_MissingWithoutCreate_ReturnsNull()
    {
        var key = _navigator.ResolveKey("DoesNotExist", createMissing: false);
        Assert.Null(key);
    }

    [Fact]
    public void ResolveKey_Idempotent()
    {
        _navigator.ResolveKey("Key1", createMissing: true);
        _navigator.ResolveKey("Key1", createMissing: true); // should not throw
        var key = _navigator.ResolveKey("Key1", createMissing: false);
        Assert.NotNull(key);
    }

    [Fact]
    public void GetChildren_ReturnsAllSiblings()
    {
        _navigator.ResolveKey("A", createMissing: true);
        _navigator.ResolveKey("B", createMissing: true);
        _navigator.ResolveKey("C", createMissing: true);

        var root = _pages.ReadHeader().RootKeyPage;
        var children = _navigator.GetChildren(root).ToList();
        Assert.Equal(3, children.Count);
        Assert.Contains(children, k => k.KeyName == "A");
        Assert.Contains(children, k => k.KeyName == "B");
        Assert.Contains(children, k => k.KeyName == "C");
    }

    [Fact]
    public void ResolveKey_WithForwardSlashes()
    {
        _navigator.ResolveKey("Foo/Bar/Baz", createMissing: true);
        var key = _navigator.ResolveKey(@"Foo\Bar\Baz", createMissing: false);
        Assert.NotNull(key);
        Assert.Equal("Baz", key!.KeyName);
    }
}
