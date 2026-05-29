using System.Security.Cryptography;
using HiveDB.Storage;
using HiveDB.Tree;
using HiveDB.Value;

namespace HiveDB;

public sealed class RegistryDatabase : IDisposable
{
    private readonly BinaryFileHandle _file;
    private readonly PageManager _pages;
    private readonly TreeNavigator _navigator;
    private readonly bool _readOnly;
    private bool _disposed;

    private RegistryDatabase(BinaryFileHandle file, PageManager pages, TreeNavigator navigator, bool readOnly)
    {
        _file = file;
        _pages = pages;
        _navigator = navigator;
        _readOnly = readOnly;
    }

    // ── Factory methods ─────────────────────────────────

    public static RegistryDatabase Create(string filePath, string? password = null, bool signOnly = false)
    {
        var file = new BinaryFileHandle(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var cache = new PageCache();

        CryptoManager? crypto = null;
        EncryptionHeader? encHeader = null;

        if (password != null)
        {
            var mode = signOnly ? ProtectionMode.Signed : ProtectionMode.Encrypted;

            // GCM encryption requires .NET 8.0+
            if (mode == ProtectionMode.Encrypted && !CryptoManager.IsGcmAvailable)
                throw new PlatformNotSupportedException(
                    "AES-GCM encryption is not available on this platform. " +
                    "Use signOnly: true for HMAC signing, or target .NET 8.0+.");

            byte[] salt = CryptoManager.GenerateSalt();
            byte[] key = CryptoManager.DeriveKey(password, salt, CryptoManager.DefaultIterations);
            crypto = new CryptoManager(key, mode);
            encHeader = new EncryptionHeader
            {
                Mode = mode,
                Salt = salt,
                KeyCheckHash = CryptoManager.ComputeKeyCheckHash(key),
                Pbkdf2Iterations = CryptoManager.DefaultIterations,
            };
            KeyPage.MaxDataSize = CryptoManager.MaxDataSize;
        }
        else
        {
            KeyPage.MaxDataSize = FileHeader.PageSize;
        }

        var pages = new PageManager(file, cache, crypto);
        var navigator = new TreeNavigator(pages);

        // Write header at page 0 first (so file starts at offset 0)
        var header = FileHeader.CreateNew(rootKeyPage: 1, encHeader);
        var headerBuffer = new byte[FileHeader.PageSize];
        header.Write(headerBuffer);
        file.WritePage(0, headerBuffer);

        // Write root key page at page 1 (via PageManager to compute CRC16 or encrypt)
        var rootKeyBuffer = new byte[FileHeader.PageSize];
        var rootKey = new KeyPage
        {
            PageNumber = 1,
            KeyName = string.Empty,
            ParentPage = 0,
            FirstChildPage = 0,
            NextSiblingPage = 0,
        };
        rootKey.Write(rootKeyBuffer);
        pages.WritePage(1, rootKeyBuffer);

        return new RegistryDatabase(file, pages, navigator, readOnly: false);
    }

    public static RegistryDatabase Open(string filePath, bool readOnly = false, string? password = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Database file not found.", filePath);

        var file = new BinaryFileHandle(
            filePath,
            FileMode.Open,
            readOnly ? FileAccess.Read : FileAccess.ReadWrite,
            readOnly ? FileShare.Read : FileShare.None);

        var cache = new PageCache();
        var pages = new PageManager(file, cache); // crypto = null initially

        // Read header to detect protection
        var header = pages.ReadHeader();

        if (header.Encryption is { IsProtected: true } enc)
        {
            if (password == null)
            {
                string desc = enc.Mode == ProtectionMode.Signed ? "签名" : "加密";
                throw new HiveDBException($"数据库已{desc}，需要提供密码。");
            }

            byte[] key = CryptoManager.DeriveKey(
                password, enc.Salt, enc.Pbkdf2Iterations);

            byte[] expectedHash = enc.KeyCheckHash;
            byte[] actualHash = CryptoManager.ComputeKeyCheckHash(key);

            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new HiveDBException("密码错误。");

            pages.CryptoManager = new CryptoManager(key, enc.Mode);
            KeyPage.MaxDataSize = CryptoManager.MaxDataSize;
        }
        else
        {
            KeyPage.MaxDataSize = FileHeader.PageSize;

            if (password != null)
                throw new HiveDBException("数据库未受保护，但提供了密码。");
        }

        var navigator = new TreeNavigator(pages);
        return new RegistryDatabase(file, pages, navigator, readOnly);
    }

    // ── Public properties ───────────────────────────────

    public RegistryKey RootKey
    {
        get
        {
            ThrowIfDisposed();
#pragma warning disable CA2000 // Dispose is a no-op on RegistryKey for handles we don't own
            return new RegistryKey(this, _pages, _navigator,
                _pages.ReadHeader().RootKeyPage, string.Empty);
#pragma warning restore CA2000
        }
    }

    public bool IsReadOnly => _readOnly;
    public long FileSize => _file.Length;

    // ── Path-based convenience methods ──────────────────

    public object? GetValue(string path, string name, object? defaultValue = null)
    {
        ThrowIfDisposed();
        using var rl = _pages.ReadLock();
        var key = _navigator.ResolveKey(path, createMissing: false);
        if (key is null)
            return defaultValue;

        var entry = key.FindValue(name);
        if (entry is null)
            return defaultValue;

        byte[] data = entry.OverflowPage != 0
            ? _pages.ReadOverflowChain(entry.OverflowPage, entry.DataLength)
            : entry.InlineData;

        return ValueSerializer.Deserialize(entry.Kind, data);
    }

    public RegistryValueKind GetValueKind(string path, string name)
    {
        ThrowIfDisposed();
        using var rl = _pages.ReadLock();
        var key = _navigator.ResolveKey(path, createMissing: false)
            ?? throw new KeyNotFoundException($"Key '{path}' not found.");
        var entry = key.FindValue(name)
            ?? throw new KeyNotFoundException($"Value '{name}' not found.");
        return entry.Kind;
    }

    public void CreateKey(string path)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        using var wl = _pages.WriteLock();
        _navigator.ResolveKey(path, createMissing: true);
    }

    public void DeleteKey(string path, bool recursive = false)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        // Use RootKey.DeleteSubKey for proper implementation
        // Parse path into parent path + name
        int idx = path.LastIndexOf('\\');
        string parentPath = idx < 0 ? string.Empty : path[..idx];
        string name = idx < 0 ? path : path[(idx + 1)..];

        using var key = idx < 0 ? RootKey : OpenKey(parentPath)
            ?? throw new KeyNotFoundException($"Parent key for '{path}' not found.");
        key.DeleteSubKey(name, recursive);
    }

    public bool KeyExists(string path)
    {
        ThrowIfDisposed();
        using var rl = _pages.ReadLock();
        return _navigator.ResolveKey(path, createMissing: false) is not null;
    }

    public string[] GetSubKeyNames(string path)
    {
        ThrowIfDisposed();
        using var rl = _pages.ReadLock();
        var key = _navigator.ResolveKey(path, createMissing: false)
            ?? throw new KeyNotFoundException($"Key '{path}' not found.");
        return _navigator.GetChildren(key.PageNumber)
            .Select(k => k.KeyName)
            .ToArray();
    }

    public void SetValue(string path, string name, object value, RegistryValueKind kind)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        using var wl = _pages.WriteLock();
        var key = _navigator.ResolveKey(path, createMissing: false)
            ?? throw new KeyNotFoundException($"Key '{path}' not found.");

        byte[] data = ValueSerializer.Serialize(kind, value);

        var existing = key.FindValue(name);
        if (existing is not null)
        {
            if (existing.OverflowPage != 0)
                _pages.FreeOverflowChain(existing.OverflowPage);
            key.RemoveValue(name);
        }

        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);

        if (key.CanFitInline(nameBytes.Length, data.Length))
        {
            key.AddInlineValue(name, kind, data);
        }
        else if (key.CanFitEntry(nameBytes.Length))
        {
            int overflowPage = _pages.WriteOverflowChain(data, 0, data.Length);
            key.AddOverflowValue(name, kind, data.Length, overflowPage);
        }
        else
        {
            throw new InvalidOperationException(
                $"Key page is full: cannot add more values to '{path}'. " +
                $"Page has {key.Values.Count} values and no room for entry metadata.");
        }

        _pages.WritePage(key.PageNumber, SerializeKeyPage(key));
    }

    public void DeleteValue(string path, string name)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        using var wl = _pages.WriteLock();
        var key = _navigator.ResolveKey(path, createMissing: false)
            ?? throw new KeyNotFoundException($"Key '{path}' not found.");

        var entry = key.FindValue(name);
        if (entry is not null && entry.OverflowPage != 0)
            _pages.FreeOverflowChain(entry.OverflowPage);

        key.RemoveValue(name);
        _pages.WritePage(key.PageNumber, SerializeKeyPage(key));
    }

    public string[] GetValueNames(string path)
    {
        ThrowIfDisposed();
        using var rl = _pages.ReadLock();
        var key = _navigator.ResolveKey(path, createMissing: false)
            ?? throw new KeyNotFoundException($"Key '{path}' not found.");
        return key.Values.Select(v => v.Name).ToArray();
    }

    // ── Flush & Dispose ─────────────────────────────────

    public void Flush()
    {
        ThrowIfDisposed();
        _file.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _pages.Dispose();
        _disposed = true;
    }

    // ── Internal ────────────────────────────────────────

    internal void ThrowIfReadOnly()
    {
        if (_readOnly)
            throw new InvalidOperationException("Cannot write to a read-only database.");
    }

    private RegistryKey? OpenKey(string path)
    {
        var resolved = _navigator.ResolveKey(path, createMissing: false);
        return resolved is null ? null
            : new RegistryKey(this, _pages, _navigator, resolved.PageNumber, path);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RegistryDatabase));
    }

    private static byte[] SerializeKeyPage(KeyPage page)
    {
        var buffer = new byte[FileHeader.PageSize];
        page.Write(buffer);
        return buffer;
    }
}
