using HiveDB.Storage;

namespace HiveDB.Tests;

public class CorruptionTests : IDisposable
{
    private readonly string _filePath;

    public CorruptionTests()
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
    public void Truncated_File_Throws_On_Open()
    {
        // Create a valid file first
        using (var db = RegistryDatabase.Create(_filePath)) { }

        // Truncate it
        using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(100); // way too small
        }

        Assert.ThrowsAny<Exception>(() => RegistryDatabase.Open(_filePath));
    }

    [Fact]
    public void Bad_Magic_Throws()
    {
        // Create a valid file
        using (var db = RegistryDatabase.Create(_filePath)) { }

        // Corrupt the magic bytes
        using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(0, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
            fs.WriteByte(0xFF);
            fs.WriteByte(0xFF);
            fs.WriteByte(0xFF);
        }

        var ex = Assert.Throws<HiveDBException>(() => RegistryDatabase.Open(_filePath));
        Assert.Contains("magic", ex.Message);
    }

    [Fact]
    public void Header_CRC_Mismatch_Throws()
    {
        // Create a valid file
        using (var db = RegistryDatabase.Create(_filePath)) { }

        // Corrupt the stored CRC-32 field at offset 24
        using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(24, SeekOrigin.Begin); // CRC-32 field
            fs.WriteByte(0xFF);
        }

        var ex = Assert.Throws<HiveDBException>(() => RegistryDatabase.Open(_filePath));
        Assert.Contains("CRC", ex.Message);
    }

    [Fact]
    public void Key_Page_CRC_Mismatch_Throws()
    {
        // Create a valid file
        using (var db = RegistryDatabase.Create(_filePath))
        {
            db.CreateKey("Test");
        }

        // Corrupt a key page
        using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(FileHeader.PageSize + 30, SeekOrigin.Begin); // somewhere in key data
            fs.WriteByte(0xFF);
            fs.WriteByte(0xFF);
        }

        Assert.Throws<HiveDBException>(() =>
        {
            using var db = RegistryDatabase.Open(_filePath);
            db.GetValue("Test", "DoesNotExist");
        });
    }
}
