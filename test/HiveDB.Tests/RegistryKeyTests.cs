namespace HiveDB.Tests;

public class RegistryKeyTests : IDisposable
{
    private readonly string _filePath;

    public RegistryKeyTests()
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
    public void RootKey_Name_Is_Empty()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        Assert.Equal("", root.Name);
        Assert.Equal("", root.FullPath);
    }

    [Fact]
    public void CreateSubKey_Returns_Key_With_Correct_Path()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        using var sub = root.CreateSubKey("Software");
        Assert.Equal("Software", sub.Name);
        Assert.Equal("Software", sub.FullPath);
    }

    [Fact]
    public void CreateSubKey_Nested()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        using var app = root.CreateSubKey(@"Software\MyApp\Settings");
        Assert.Equal("Settings", app.Name);
        Assert.Equal(@"Software\MyApp\Settings", app.FullPath);
    }

    [Fact]
    public void OpenSubKey_Returns_Existing_Key()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        root.CreateSubKey("Test");
        using var opened = root.OpenSubKey("Test");
        Assert.NotNull(opened);
        Assert.Equal("Test", opened!.Name);
    }

    [Fact]
    public void OpenSubKey_Missing_Returns_Null()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        using var opened = root.OpenSubKey("Missing");
        Assert.Null(opened);
    }

    [Fact]
    public void HasSubKey_Returns_Correctly()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        root.CreateSubKey("Exists");
        Assert.True(root.HasSubKey("Exists"));
        Assert.False(root.HasSubKey("Missing"));
    }

    [Fact]
    public void GetSubKeyNames_Returns_Names()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        root.CreateSubKey("A");
        root.CreateSubKey("B");
        string[] names = root.GetSubKeyNames();
        Assert.Equal(2, names.Length);
        Assert.Contains("A", names);
        Assert.Contains("B", names);
    }

    [Fact]
    public void SetValue_And_GetValue()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        using var key = root.CreateSubKey("Data");
        key.SetValue("StringVal", "hello");
        Assert.Equal("hello", key.GetValue("StringVal"));
    }

    [Fact]
    public void GetValue_Type_Inference()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        using var key = root.CreateSubKey("Data");
        key.SetValue("Num", 42);
        Assert.Equal(42, key.GetValue("Num"));
    }

    [Fact]
    public void GetValue_With_Default()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        Assert.Equal("default", root.GetValue("Missing", "default"));
    }

    [Fact]
    public void DeleteValue_Removes_It()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        root.SetValue("TempVal", 123);
        root.DeleteValue("TempVal");
        Assert.Null(root.GetValue("TempVal"));
    }

    [Fact]
    public void GetValueNames_Returns_All()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        root.SetValue("Val1", "a");
        root.SetValue("Val2", 2);
        string[] names = root.GetValueNames();
        Assert.Equal(2, names.Length);
    }

    [Fact]
    public void DeleteSubKey_Removes_Key()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        root.CreateSubKey("ToDelete");
        root.DeleteSubKey("ToDelete");
        Assert.False(root.HasSubKey("ToDelete"));
    }

    [Fact]
    public void ParentKey_Returns_Parent()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        using var child = root.CreateSubKey("Child");
        using var parent = child.ParentKey;
        Assert.NotNull(parent);
        Assert.Equal("", parent!.Name);
    }

    [Fact]
    public void ParentKey_Of_Root_Is_Null()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        Assert.Null(root.ParentKey);
    }

    [Fact]
    public void Database_Property_Returns_Database()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var root = db.RootKey;
        Assert.Same(db, root.Database);
    }

    [Fact]
    public void Disposed_Key_Throws()
    {
        using var db = RegistryDatabase.Create(_filePath);
        var key = db.RootKey;
        key.Dispose();
        Assert.Throws<ObjectDisposedException>(() => key.GetValue("anything"));
    }

    [Fact]
    public void Generic_GetValue_Extension_Works()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var key = db.RootKey;
        key.SetValue("intVal", 42);
        int? result = key.GetValue<int>("intVal");
        Assert.Equal(42, result);
    }

    [Fact]
    public void SetValue_Large_Binary_Overflow()
    {
        using var db = RegistryDatabase.Create(_filePath);
        using var key = db.RootKey;
        var data = new byte[10000];
        new Random(42).NextBytes(data);
        key.SetValue("big", data, RegistryValueKind.Binary);
        byte[]? result = (byte[]?)key.GetValue("big");
        Assert.Equal(data, result);
    }
}
