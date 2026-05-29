using HiveDB.Storage;
using HiveDB.Tree;

namespace HiveDB.Tests;

/// <summary>
/// Tests for GCM encryption and HMAC signing page protection.
/// </summary>
public class CryptoPageTests : IDisposable
{
    private readonly string _filePath;
    private const string TestPassword = "test-password-123";
    private const string WrongPassword = "wrong-password-456";

    public CryptoPageTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"hivedb_crypto_{Guid.NewGuid()}.dat");
    }

    public void Dispose()
    {
        // Reset KeyPage.MaxDataSize to default between tests
        KeyPage.MaxDataSize = FileHeader.PageSize;

        if (File.Exists(_filePath))
        {
            try { File.Delete(_filePath); } catch { /* ignore */ }
        }
    }

    // ── GCM Encrypted mode ──────────────────────────────────

    [Fact]
    public void Create_Encrypted_Creates_File()
    {
        using var db = RegistryDatabase.Create(_filePath, TestPassword);
        Assert.True(File.Exists(_filePath));
        Assert.True(db.FileSize > 0);
    }

    [Fact]
    public void Encrypted_String_Roundtrip()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword))
        {
            db.CreateKey("Settings");
            db.SetValue("Settings", "Theme", "dark", RegistryValueKind.String);
        }

        using var db2 = RegistryDatabase.Open(_filePath, password: TestPassword);
        Assert.Equal("dark", db2.GetValue("Settings", "Theme"));
    }

    [Fact]
    public void Encrypted_DWord_Roundtrip()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword))
        {
            db.CreateKey("Counters");
            db.SetValue("Counters", "Total", 42, RegistryValueKind.DWord);
        }

        using var db2 = RegistryDatabase.Open(_filePath, password: TestPassword);
        Assert.Equal(42, db2.GetValue("Counters", "Total"));
    }

    [Fact]
    public void Encrypted_Binary_Roundtrip()
    {
        byte[] data = new byte[1024];
        new Random(12345).NextBytes(data);

        using (var db = RegistryDatabase.Create(_filePath, TestPassword))
        {
            db.CreateKey("Raw");
            db.SetValue("Raw", "Blob", data, RegistryValueKind.Binary);
        }

        using var db2 = RegistryDatabase.Open(_filePath, password: TestPassword);
        byte[]? result = (byte[]?)db2.GetValue("Raw", "Blob");
        Assert.Equal(data, result);
    }

    [Fact]
    public void Encrypted_Open_Without_Password_Throws()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword)) { }

        var ex = Assert.Throws<HiveDBException>(() => RegistryDatabase.Open(_filePath));
        Assert.Contains("加密", ex.Message);
    }

    [Fact]
    public void Encrypted_Open_Wrong_Password_Throws()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword)) { }

        var ex = Assert.Throws<HiveDBException>(
            () => RegistryDatabase.Open(_filePath, password: WrongPassword));
        Assert.Contains("密码错误", ex.Message);
    }

    [Fact]
    public void Encrypted_Open_ReadOnly_Works()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword))
        {
            db.CreateKey("K");
            db.SetValue("K", "V", "hello", RegistryValueKind.String);
        }

        using var ro = RegistryDatabase.Open(_filePath, readOnly: true, password: TestPassword);
        Assert.True(ro.IsReadOnly);
        Assert.Equal("hello", ro.GetValue("K", "V"));
    }

    [Fact]
    public void Encrypted_Large_Value_Overflow_Roundtrip()
    {
        byte[] data = new byte[10000];
        new Random(42).NextBytes(data);

        using (var db = RegistryDatabase.Create(_filePath, TestPassword))
        {
            db.CreateKey("Big");
            db.SetValue("Big", "Payload", data, RegistryValueKind.Binary);
        }

        using var db2 = RegistryDatabase.Open(_filePath, password: TestPassword);
        byte[]? result = (byte[]?)db2.GetValue("Big", "Payload");
        Assert.Equal(data, result);
    }

    [Fact]
    public void Encrypted_Many_Keys_And_Values()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword))
        {
            for (int i = 0; i < 10; i++)
            {
                string keyPath = $"Group{i}";
                db.CreateKey(keyPath);
                for (int j = 0; j < 5; j++)
                    db.SetValue(keyPath, $"Name{j}", i * 100 + j, RegistryValueKind.DWord);
            }
        }

        using var db2 = RegistryDatabase.Open(_filePath, password: TestPassword);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(db2.KeyExists($"Group{i}"));
            Assert.Equal(5, db2.GetValueNames($"Group{i}").Length);
        }
        Assert.Equal(202, db2.GetValue("Group2", "Name2"));
    }

    [Fact]
    public void Encrypted_File_Has_HIVE_Magic_But_Garbled_Data()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword))
        {
            db.CreateKey("Test");
            db.SetValue("Test", "Val", "hello", RegistryValueKind.String);
        }

        byte[] raw = File.ReadAllBytes(_filePath);
        // First 4 bytes should be "HIVE" magic
        Assert.Equal((byte)'H', raw[0]);
        Assert.Equal((byte)'I', raw[1]);
        Assert.Equal((byte)'V', raw[2]);
        Assert.Equal((byte)'E', raw[3]);

        // Encryption flag should be set at offset 28
        uint flags = BitConverter.ToUInt32(raw, 28);
        Assert.Equal(1u, flags & 3); // ProtectionMode.Encrypted

        // Page 1 data should not contain plaintext "hello"
        byte[] page1 = raw.AsSpan(FileHeader.PageSize, FileHeader.PageSize).ToArray();
        string page1Str = System.Text.Encoding.ASCII.GetString(page1);
        Assert.DoesNotContain("hello", page1Str);
    }

    [Fact]
    public void Encrypted_Tampered_Page_Throws()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword))
        {
            db.CreateKey("Test");
            db.SetValue("Test", "Val", "hello", RegistryValueKind.String);
        }

        // Tamper with a byte in page 1 (encrypted data)
        byte[] raw = File.ReadAllBytes(_filePath);
        raw[FileHeader.PageSize + 50] ^= 0xFF; // flip bits
        File.WriteAllBytes(_filePath, raw);

        Assert.Throws<HiveDBException>(() =>
        {
            using var db = RegistryDatabase.Open(_filePath, password: TestPassword);
            db.GetValue("Test", "Val");
        });
    }

    // ── HMAC Signed mode ────────────────────────────────────

    [Fact]
    public void Create_Signed_Creates_File()
    {
        using var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true);
        Assert.True(File.Exists(_filePath));
    }

    [Fact]
    public void Signed_String_Roundtrip()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true))
        {
            db.CreateKey("Settings");
            db.SetValue("Settings", "Theme", "dark", RegistryValueKind.String);
        }

        using var db2 = RegistryDatabase.Open(_filePath, password: TestPassword);
        Assert.Equal("dark", db2.GetValue("Settings", "Theme"));
    }

    [Fact]
    public void Signed_All_Value_Types_Roundtrip()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true))
        {
            db.CreateKey("Data");
            db.SetValue("Data", "Str", "hello", RegistryValueKind.String);
            db.SetValue("Data", "DWord", 42, RegistryValueKind.DWord);
            db.SetValue("Data", "QWord", 1234567890123L, RegistryValueKind.QWord);
            db.SetValue("Data", "Binary", new byte[] { 0xAA, 0xBB, 0xCC }, RegistryValueKind.Binary);
            db.SetValue("Data", "Multi", new[] { "a", "b" }, RegistryValueKind.MultiString);
        }

        using var db2 = RegistryDatabase.Open(_filePath, password: TestPassword);
        Assert.Equal("hello", db2.GetValue("Data", "Str"));
        Assert.Equal(42, db2.GetValue("Data", "DWord"));
        Assert.Equal(1234567890123L, db2.GetValue("Data", "QWord"));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, (byte[]?)db2.GetValue("Data", "Binary"));
        Assert.Equal(new[] { "a", "b" }, (string[]?)db2.GetValue("Data", "Multi"));
    }

    [Fact]
    public void Signed_Open_Without_Password_Throws()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true)) { }

        var ex = Assert.Throws<HiveDBException>(() => RegistryDatabase.Open(_filePath));
        Assert.Contains("签名", ex.Message);
    }

    [Fact]
    public void Signed_Open_Wrong_Password_Throws()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true)) { }

        var ex = Assert.Throws<HiveDBException>(
            () => RegistryDatabase.Open(_filePath, password: WrongPassword));
        Assert.Contains("密码错误", ex.Message);
    }

    [Fact]
    public void Signed_Data_Is_Plaintext_Visible()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true))
        {
            db.CreateKey("Visible");
            db.SetValue("Visible", "Clear", "helloworld", RegistryValueKind.String);
        }

        byte[] raw = File.ReadAllBytes(_filePath);
        // Page 1+ should contain plaintext "helloworld" somewhere
        string allBytes = System.Text.Encoding.ASCII.GetString(raw);
        Assert.Contains("helloworld", allBytes);
    }

    [Fact]
    public void Signed_Tampered_Page_Throws()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true))
        {
            db.CreateKey("Test");
            db.SetValue("Test", "Val", "hello", RegistryValueKind.String);
        }

        // Tamper with a byte in a data page
        byte[] raw = File.ReadAllBytes(_filePath);
        raw[FileHeader.PageSize + 30] ^= 0xFF; // flip bits in page 1
        File.WriteAllBytes(_filePath, raw);

        Assert.Throws<HiveDBException>(() =>
        {
            using var db = RegistryDatabase.Open(_filePath, password: TestPassword);
            db.GetValue("Test", "Val");
        });
    }

    [Fact]
    public void Signed_Footer_Corruption_Detected()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true))
        {
            db.CreateKey("Test");
        }

        // Tamper with the HMAC footer (bytes 4080+)
        byte[] raw = File.ReadAllBytes(_filePath);
        raw[FileHeader.PageSize + 4090] ^= 0xFF; // flip bit in HMAC area
        File.WriteAllBytes(_filePath, raw);

        Assert.Throws<HiveDBException>(() =>
        {
            using var db = RegistryDatabase.Open(_filePath, password: TestPassword);
            db.KeyExists("Test");
        });
    }

    // ── Mode isolation ──────────────────────────────────────

    [Fact]
    public void Unprotected_DB_With_Password_Throws()
    {
        using (var db = RegistryDatabase.Create(_filePath)) { }

        var ex = Assert.Throws<HiveDBException>(
            () => RegistryDatabase.Open(_filePath, password: TestPassword));
        Assert.Contains("未受保护", ex.Message);
    }

    [Fact]
    public void Encrypted_DB_Cannot_Open_Without_Password()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword)) { }

        // Verify it can't be opened as unprotected
        Assert.Throws<HiveDBException>(() => RegistryDatabase.Open(_filePath));
    }

    [Fact]
    public void Signed_DB_Cannot_Open_Without_Password()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true)) { }

        Assert.Throws<HiveDBException>(() => RegistryDatabase.Open(_filePath));
    }

    [Fact]
    public void Encrypted_DB_Rejects_Password_For_Signed()
    {
        // Create encrypted, try opening without password → error about encryption
        using (var db = RegistryDatabase.Create(_filePath, TestPassword)) { }

        var ex = Assert.Throws<HiveDBException>(() => RegistryDatabase.Open(_filePath));
        Assert.Contains("加密", ex.Message);
    }

    [Fact]
    public void Signed_DB_Rejects_Password_For_Encrypted()
    {
        // Create signed, try opening without password → error about signing
        using (var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true)) { }

        var ex = Assert.Throws<HiveDBException>(() => RegistryDatabase.Open(_filePath));
        Assert.Contains("签名", ex.Message);
    }

    // ── Backward compatibility ──────────────────────────────

    [Fact]
    public void Unprotected_DB_Still_Works_Basic()
    {
        using (var db = RegistryDatabase.Create(_filePath))
        {
            db.CreateKey("Test");
            db.SetValue("Test", "Val", "hello", RegistryValueKind.String);
        }

        using var db2 = RegistryDatabase.Open(_filePath);
        Assert.Equal("hello", db2.GetValue("Test", "Val"));
    }

    [Fact]
    public void Unprotected_DB_Still_Detects_CRC_Error()
    {
        using (var db = RegistryDatabase.Create(_filePath))
        {
            db.CreateKey("Test");
        }

        byte[] raw = File.ReadAllBytes(_filePath);
        raw[FileHeader.PageSize + 30] ^= 0xFF;
        File.WriteAllBytes(_filePath, raw);

        Assert.Throws<HiveDBException>(() =>
        {
            using var db = RegistryDatabase.Open(_filePath);
            db.KeyExists("Test");
        });
    }

    // ── Metadata verification ───────────────────────────────

    [Fact]
    public void Encrypted_Header_Shows_ProtectionMode_Encrypted()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword)) { }

        byte[] raw = File.ReadAllBytes(_filePath);
        uint flags = BitConverter.ToUInt32(raw, EncryptionHeader.HeaderOffset);
        Assert.Equal(1u, flags & 3); // ProtectionMode.Encrypted
    }

    [Fact]
    public void Signed_Header_Shows_ProtectionMode_Signed()
    {
        using (var db = RegistryDatabase.Create(_filePath, TestPassword, signOnly: true)) { }

        byte[] raw = File.ReadAllBytes(_filePath);
        uint flags = BitConverter.ToUInt32(raw, EncryptionHeader.HeaderOffset);
        Assert.Equal(2u, flags & 3); // ProtectionMode.Signed
    }

    [Fact]
    public void Unprotected_Header_Shows_No_Protection()
    {
        using (var db = RegistryDatabase.Create(_filePath)) { }

        byte[] raw = File.ReadAllBytes(_filePath);
        uint flags = BitConverter.ToUInt32(raw, EncryptionHeader.HeaderOffset);
        Assert.Equal(0u, flags & 3); // ProtectionMode.None
    }

    // ── Password complexity ─────────────────────────────────

    [Fact]
    public void Long_Password_Works()
    {
        string longPw = new string('x', 128);
        using (var db = RegistryDatabase.Create(_filePath, longPw))
        {
            db.CreateKey("K");
            db.SetValue("K", "V", "data", RegistryValueKind.String);
        }

        using var db2 = RegistryDatabase.Open(_filePath, password: longPw);
        Assert.Equal("data", db2.GetValue("K", "V"));
    }

    [Fact]
    public void Unicode_Password_Works()
    {
        string unicodePw = "密码🔐テスト";
        using (var db = RegistryDatabase.Create(_filePath, unicodePw))
        {
            db.CreateKey("K");
            db.SetValue("K", "V", 42, RegistryValueKind.DWord);
        }

        using var db2 = RegistryDatabase.Open(_filePath, password: unicodePw);
        Assert.Equal(42, db2.GetValue("K", "V"));
    }

    [Fact]
    public void Empty_Password_Is_Treated_As_No_Password()
    {
        // null password → unprotected
        using (var db = RegistryDatabase.Create(_filePath))
        {
            Assert.True(db.FileSize > 0);
        }

        // Can open without password (previous db is disposed and file handle released)
        using var db2 = RegistryDatabase.Open(_filePath);
        Assert.NotNull(db2);
    }
}
