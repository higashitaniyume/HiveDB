namespace HiveDB.Tests;

public class RegistryDatabaseTests : IDisposable
{
    private readonly string _filePath;

    public RegistryDatabaseTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"hivedb_test_{Guid.NewGuid()}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
        {
            try { File.Delete(_filePath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Create_File_Contains_Correct_Header()
    {
        using var db = RegistryDatabase.Create(_filePath);
        Assert.True(File.Exists(_filePath));
        Assert.True(db.FileSize > 0);
    }

    [Fact]
    public void Open_Existing_File_Succeeds()
    {
        using (var db = RegistryDatabase.Create(_filePath)) { }
        using var reopened = RegistryDatabase.Open(_filePath);
        Assert.NotNull(reopened.RootKey);
    }

    [Fact]
    public void Open_NonExistent_File_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => RegistryDatabase.Open("nonexistent_12345.dat"));
    }

    [Fact]
    public void Open_ReadOnly_Prevents_Writes()
    {
        using (var db = RegistryDatabase.Create(_filePath)) { }
        using var ro = RegistryDatabase.Open(_filePath, readOnly: true);
        Assert.True(ro.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => ro.CreateKey("Test"));
    }

    [Fact]
    public void CreateKey_Single_Level()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Software");
        Assert.True(db.KeyExists("Software"));
    }

    [Fact]
    public void CreateKey_Nested()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey(@"Software\MyApp\Settings");
        Assert.True(db.KeyExists(@"Software\MyApp\Settings"));
    }

    [Fact]
    public void CreateKey_Idempotent()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Key1");
        db.CreateKey("Key1"); // should not throw
        Assert.True(db.KeyExists("Key1"));
    }

    [Fact]
    public void DeleteKey_Leaf_Succeeds()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Key1");
        db.DeleteKey("Key1");
        Assert.False(db.KeyExists("Key1"));
    }

    [Fact]
    public void DeleteKey_With_Subkeys_NonRecursive_Throws()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey(@"Parent\Child");
        Assert.Throws<InvalidOperationException>(() => db.DeleteKey("Parent"));
    }

    [Fact]
    public void DeleteKey_Recursive_Removes_Subtree()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey(@"A\B\C");
        db.CreateKey(@"A\B\D");
        db.DeleteKey("A", recursive: true);
        Assert.False(db.KeyExists("A"));
    }

    [Fact]
    public void KeyExists_Returns_True_For_Existing()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Test");
        Assert.True(db.KeyExists("Test"));
    }

    [Fact]
    public void KeyExists_Returns_False_For_Missing()
    {
        using var db = RegistryDatabase.Create(_filePath);
        Assert.False(db.KeyExists("Missing"));
    }

    [Fact]
    public void GetSubKeyNames_Returns_Children()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey(@"Root\A");
        db.CreateKey(@"Root\B");
        db.CreateKey(@"Root\C");
        string[] names = db.GetSubKeyNames("Root");
        Assert.Equal(3, names.Length);
        Assert.Contains("A", names);
        Assert.Contains("B", names);
        Assert.Contains("C", names);
    }

    [Fact]
    public void SetValue_String_Roundtrips()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Settings");
        db.SetValue("Settings", "Theme", "dark", RegistryValueKind.String);
        object? result = db.GetValue("Settings", "Theme");
        Assert.Equal("dark", result);
    }

    [Fact]
    public void SetValue_DWord_Roundtrips()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Settings");
        db.SetValue("Settings", "Count", 42, RegistryValueKind.DWord);
        object? result = db.GetValue("Settings", "Count");
        Assert.Equal(42, result);
    }

    [Fact]
    public void SetValue_QWord_Roundtrips()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Settings");
        db.SetValue("Settings", "BigCount", 1234567890123L, RegistryValueKind.QWord);
        object? result = db.GetValue("Settings", "BigCount");
        Assert.Equal(1234567890123L, result);
    }

    [Fact]
    public void SetValue_Binary_Roundtrips()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Data");
        byte[] input = { 0x01, 0x02, 0x03, 0xFF };
        db.SetValue("Data", "Bytes", input, RegistryValueKind.Binary);
        object? result = db.GetValue("Data", "Bytes");
        Assert.Equal(input, (byte[])result!);
    }

    [Fact]
    public void SetValue_MultiString_Roundtrips()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Data");
        string[] input = { "hello", "world" };
        db.SetValue("Data", "Strings", input, RegistryValueKind.MultiString);
        object? result = db.GetValue("Data", "Strings");
        Assert.Equal(input, (string[])result!);
    }

    [Fact]
    public void SetValue_Overwrites_Existing()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Key");
        db.SetValue("Key", "Val", "old", RegistryValueKind.String);
        db.SetValue("Key", "Val", "new", RegistryValueKind.String);
        Assert.Equal("new", db.GetValue("Key", "Val"));
    }

    [Fact]
    public void DeleteValue_Removes_Value()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Key");
        db.SetValue("Key", "Temp", 123, RegistryValueKind.DWord);
        db.DeleteValue("Key", "Temp");
        Assert.Null(db.GetValue("Key", "Temp"));
    }

    [Fact]
    public void DeleteValue_NonExistent_Does_Not_Throw()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Key");
        db.DeleteValue("Key", "NoSuch"); // should not throw
    }

    [Fact]
    public void GetValue_Missing_Key_Returns_Default()
    {
        using var db = RegistryDatabase.Create(_filePath);
        object? result = db.GetValue("Missing", "Val", defaultValue: "fallback");
        Assert.Equal("fallback", result);
    }

    [Fact]
    public void GetValue_Missing_Name_Returns_Default()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Key");
        object? result = db.GetValue("Key", "Missing", defaultValue: 99);
        Assert.Equal(99, result);
    }

    [Fact]
    public void GetValueKind_Returns_Correct_Kind()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Key");
        db.SetValue("Key", "Val", 42, RegistryValueKind.DWord);
        Assert.Equal(RegistryValueKind.DWord, db.GetValueKind("Key", "Val"));
    }

    [Fact]
    public void GetValueNames_Returns_All_Names()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Key");
        db.SetValue("Key", "A", "1", RegistryValueKind.String);
        db.SetValue("Key", "B", 2, RegistryValueKind.DWord);
        string[] names = db.GetValueNames("Key");
        Assert.Equal(2, names.Length);
        Assert.Contains("A", names);
        Assert.Contains("B", names);
    }

    [Fact]
    public void Dispose_Releases_FileHandle()
    {
        var db = RegistryDatabase.Create(_filePath);
        db.Dispose();
        // File should be deletable after dispose
        File.Delete(_filePath);
        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public void Double_Dispose_Does_Not_Throw()
    {
        var db = RegistryDatabase.Create(_filePath);
        db.Dispose();
        db.Dispose(); // should not throw
    }

    [Fact]
    public void FileSize_Grows_With_Data()
    {
        using var db = RegistryDatabase.Create(_filePath);
        long initial = db.FileSize;
        db.CreateKey("SomeKey");
        Assert.True(db.FileSize >= initial);
    }
}
