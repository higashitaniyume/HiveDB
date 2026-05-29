using HiveDB.Storage;
using HiveDB.Tree;

namespace HiveDB.Tests;

public class KeyPageTests
{
    [Fact]
    public void WriteThenRead_IsIdentity()
    {
        var original = new KeyPage
        {
            PageNumber = 5,
            KeyName = "TestKey",
            ParentPage = 1,
            FirstChildPage = 10,
            NextSiblingPage = 3,
        };
        original.AddInlineValue("val1", RegistryValueKind.String, "data1"u8.ToArray());
        original.AddInlineValue("val2", RegistryValueKind.DWord, new byte[] { 42, 0, 0, 0 });

        var buffer = new byte[FileHeader.PageSize];
        original.Write(buffer);

        var parsed = KeyPage.Read(buffer, 5);
        Assert.Equal(5, parsed.PageNumber);
        Assert.Equal("TestKey", parsed.KeyName);
        Assert.Equal(1, parsed.ParentPage);
        Assert.Equal(10, parsed.FirstChildPage);
        Assert.Equal(3, parsed.NextSiblingPage);
        Assert.Equal(2, parsed.Values.Count);

        Assert.Equal("val1", parsed.Values[0].Name);
        Assert.Equal(RegistryValueKind.String, parsed.Values[0].Kind);

        Assert.Equal("val2", parsed.Values[1].Name);
        Assert.Equal(RegistryValueKind.DWord, parsed.Values[1].Kind);
    }

    [Fact]
    public void IsDeleted_Flag_IsPreserved()
    {
        var page = new KeyPage { KeyName = "x", IsDeleted = true };
        var buffer = new byte[FileHeader.PageSize];
        page.Write(buffer);
        var parsed = KeyPage.Read(buffer, 0);
        Assert.True(parsed.IsDeleted);
    }

    [Fact]
    public void CanFitInline_ReturnsTrue_WhenSpaceAvailable()
    {
        var page = new KeyPage { KeyName = "k" };
        Assert.True(page.CanFitInline(5, 100));
    }

    [Fact]
    public void CanFitInline_ReturnsFalse_WhenFull()
    {
        var page = new KeyPage { KeyName = "k" };
        // Fill most of the page
        int remaining = page.AvailableSpace - 13; // 12 header + 1 byte
        if (remaining > 0)
        {
            var bigData = new byte[remaining];
            page.AddInlineValue("big", RegistryValueKind.Binary, bigData);
        }
        Assert.False(page.CanFitInline(10, 100));
    }

    [Fact]
    public void RemoveValue_RemovesCorrectly()
    {
        var page = new KeyPage { KeyName = "k" };
        page.AddInlineValue("a", RegistryValueKind.DWord, new byte[] { 1, 0, 0, 0 });
        page.AddInlineValue("b", RegistryValueKind.DWord, new byte[] { 2, 0, 0, 0 });
        Assert.True(page.RemoveValue("a"));
        Assert.Single(page.Values);
        Assert.Equal("b", page.Values[0].Name);
    }

    [Fact]
    public void RemoveValue_NonExistent_ReturnsFalse()
    {
        var page = new KeyPage { KeyName = "k" };
        Assert.False(page.RemoveValue("nonexistent"));
    }

    [Fact]
    public void FindValue_ReturnsCorrectEntry()
    {
        var page = new KeyPage { KeyName = "k" };
        page.AddInlineValue("target", RegistryValueKind.QWord, new byte[] { 7, 0, 0, 0, 0, 0, 0, 0 });
        var found = page.FindValue("target");
        Assert.NotNull(found);
        Assert.Equal(RegistryValueKind.QWord, found!.Kind);
    }

    [Fact]
    public void FindValue_Missing_ReturnsNull()
    {
        var page = new KeyPage { KeyName = "k" };
        Assert.Null(page.FindValue("missing"));
    }

    [Fact]
    public void OverflowValue_StoresPagePointer()
    {
        var page = new KeyPage { KeyName = "k" };
        page.AddOverflowValue("large", RegistryValueKind.Binary, 10000, overflowPage: 42);
        Assert.Equal(42, page.Values[0].OverflowPage);
        Assert.Equal(10000, page.Values[0].DataLength);
        Assert.Empty(page.Values[0].InlineData);
    }

    [Fact]
    public void EmptyKeyName_RootStyle_IsPermitted()
    {
        var page = new KeyPage { KeyName = "" };
        var buffer = new byte[FileHeader.PageSize];
        page.Write(buffer);
        var parsed = KeyPage.Read(buffer, 0);
        Assert.Equal("", parsed.KeyName);
    }
}
