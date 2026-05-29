using HiveDB.Storage;
using HiveDB.Tree;
using HiveDB.Value;

namespace HiveDB;

public sealed class RegistryKey : IDisposable
{
    private readonly RegistryDatabase _database;
    private readonly PageManager _pages;
    private readonly TreeNavigator _navigator;
    private readonly int _pageNumber;
    private readonly string _fullPath;
    private bool _disposed;

    internal RegistryKey(
        RegistryDatabase database,
        PageManager pages,
        TreeNavigator navigator,
        int pageNumber,
        string fullPath)
    {
        _database = database;
        _pages = pages;
        _navigator = navigator;
        _pageNumber = pageNumber;
        _fullPath = fullPath;
    }

    public string Name
    {
        get
        {
            int idx = _fullPath.LastIndexOf('\\');
            return idx < 0 ? _fullPath : _fullPath[(idx + 1)..];
        }
    }

    public string FullPath => _fullPath;
    public RegistryDatabase Database => _database;

    public RegistryKey? ParentKey
    {
        get
        {
            using var rl = _pages.ReadLock();
            var key = ReadMyPage();
            if (key.ParentPage == 0)
                return null;
            var parent = KeyPage.Read(_pages.ReadPage(key.ParentPage), key.ParentPage);
            string parentPath = GetParentPath(_fullPath);
            return new RegistryKey(_database, _pages, _navigator, key.ParentPage, parentPath);
        }
    }

    // ── Sub-key operations ──────────────────────────────

    public RegistryKey CreateSubKey(string name)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ThrowIfInvalidName(name);

        string subPath = string.IsNullOrEmpty(_fullPath) ? name : $"{_fullPath}\\{name}";

        using var wl = _pages.WriteLock();
        var resolved = _navigator.ResolveKey(subPath, createMissing: true)!;
        return new RegistryKey(_database, _pages, _navigator, resolved.PageNumber, subPath);
    }

    public RegistryKey? OpenSubKey(string name, bool writable = false)
    {
        ThrowIfDisposed();
        if (writable) ThrowIfReadOnly();
        ThrowIfInvalidName(name);

        string subPath = string.IsNullOrEmpty(_fullPath) ? name : $"{_fullPath}\\{name}";

        using var rl = _pages.ReadLock();
        var resolved = _navigator.ResolveKey(subPath, createMissing: false);
        return resolved is null ? null : new RegistryKey(_database, _pages, _navigator, resolved.PageNumber, subPath);
    }

    public void DeleteSubKey(string name, bool recursive = false)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ThrowIfEmpty(name, "Sub-key name cannot be empty.");

        using var wl = _pages.WriteLock();
        var parent = ReadMyPage();
        var (_, target) = _navigator.FindChildWithPrev(parent, name);
        var targetKey = KeyPage.Read(_pages.ReadPage(target), target);

        if (!recursive && targetKey.FirstChildPage != 0)
            throw new InvalidOperationException($"Cannot delete key '{name}': it has sub-keys. Use recursive=true.");

        if (recursive)
        {
            // Recursively delete all children of the target
            int child = targetKey.FirstChildPage;
            while (child != 0)
            {
                var childKey = KeyPage.Read(_pages.ReadPage(child), child);
                int next = childKey.NextSiblingPage;
                DeleteSubtree(child);
                child = next;
            }
        }

        // Free overflow chains for all values in the target key
        foreach (var val in targetKey.Values)
        {
            if (val.OverflowPage != 0)
                _pages.FreeOverflowChain(val.OverflowPage);
        }

        // Unlink from parent before freeing the target page
        _navigator.UnlinkChild(parent, name);
        _pages.FreePage(target);
    }

    public string[] GetSubKeyNames()
    {
        ThrowIfDisposed();
        using var rl = _pages.ReadLock();
        return _navigator.GetChildren(_pageNumber)
            .Select(k => k.KeyName)
            .ToArray();
    }

    public bool HasSubKey(string name)
    {
        ThrowIfDisposed();
        using var rl = _pages.ReadLock();
        foreach (var child in _navigator.GetChildren(_pageNumber))
        {
            if (child.KeyName == name)
                return true;
        }
        return false;
    }

    // ── Value operations ────────────────────────────────

    public void SetValue(string name, object value)
    {
        RegistryValueKind kind = InferKind(value);
        SetValue(name, value, kind);
    }

    public void SetValue(string name, object value, RegistryValueKind kind)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ThrowIfInvalidName(name);

        byte[] data = ValueSerializer.Serialize(kind, value);

        using var wl = _pages.WriteLock();
        var key = ReadMyPage();

        // Remove existing value with same name, freeing overflow chain if any
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
        else
        {
            int overflowPage = _pages.WriteOverflowChain(data, 0, data.Length);
            key.AddOverflowValue(name, kind, data.Length, overflowPage);
        }

        _pages.WritePage(_pageNumber, SerializeKeyPage(key));
    }

    public object? GetValue(string name, object? defaultValue = null)
    {
        ThrowIfDisposed();
        ThrowIfInvalidName(name);

        using var rl = _pages.ReadLock();
        var key = ReadMyPage();
        var entry = key.FindValue(name);
        if (entry is null)
            return defaultValue;

        byte[] data = entry.OverflowPage != 0
            ? _pages.ReadOverflowChain(entry.OverflowPage, entry.DataLength)
            : entry.InlineData;

        return ValueSerializer.Deserialize(entry.Kind, data);
    }

    public RegistryValueKind GetValueKind(string name)
    {
        ThrowIfDisposed();
        ThrowIfInvalidName(name);

        using var rl = _pages.ReadLock();
        var key = ReadMyPage();
        var entry = key.FindValue(name);
        if (entry is null)
            throw new KeyNotFoundException($"Value '{name}' not found in key '{_fullPath}'.");
        return entry.Kind;
    }

    public void DeleteValue(string name)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ThrowIfInvalidName(name);

        using var wl = _pages.WriteLock();
        var key = ReadMyPage();

        var entry = key.FindValue(name);
        if (entry is not null && entry.OverflowPage != 0)
            _pages.FreeOverflowChain(entry.OverflowPage);

        key.RemoveValue(name);
        _pages.WritePage(_pageNumber, SerializeKeyPage(key));
    }

    public string[] GetValueNames()
    {
        ThrowIfDisposed();
        using var rl = _pages.ReadLock();
        return ReadMyPage().Values.Select(v => v.Name).ToArray();
    }

    // ── IDisposable ─────────────────────────────────────

    public void Dispose()
    {
        _disposed = true;
    }

    // ── Internal utils ──────────────────────────────────

    internal int PageNumber => _pageNumber;

    private KeyPage ReadMyPage() =>
        KeyPage.Read(_pages.ReadPage(_pageNumber), _pageNumber);

    private void DeleteSubtree(int pageNumber)
    {
        var key = KeyPage.Read(_pages.ReadPage(pageNumber), pageNumber);
        int child = key.FirstChildPage;
        while (child != 0)
        {
            var childKey = KeyPage.Read(_pages.ReadPage(child), child);
            int next = childKey.NextSiblingPage;
            DeleteSubtree(child);
            child = next;
        }

        foreach (var val in key.Values)
        {
            if (val.OverflowPage != 0)
                _pages.FreeOverflowChain(val.OverflowPage);
        }

        _pages.FreePage(pageNumber);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RegistryKey));
    }

    private void ThrowIfReadOnly()
    {
        _database.ThrowIfReadOnly();
    }

    private static void ThrowIfInvalidName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name cannot be null or empty.", nameof(name));
        if (System.Text.Encoding.UTF8.GetByteCount(name) > 255)
            throw new ArgumentException("Name exceeds 255 byte limit (UTF-8).", nameof(name));
    }

    private static void ThrowIfEmpty(string value, string message)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException(message);
    }

    private static RegistryValueKind InferKind(object value)
    {
        return value switch
        {
            string => RegistryValueKind.String,
            int => RegistryValueKind.DWord,
            long => RegistryValueKind.QWord,
            byte[] => RegistryValueKind.Binary,
            string[] => RegistryValueKind.MultiString,
            _ => throw new ArgumentException(
                $"Cannot infer RegistryValueKind for type {value.GetType()}. Use the overload that takes an explicit kind."),
        };
    }

    private static string GetParentPath(string path)
    {
        int idx = path.LastIndexOf('\\');
        return idx < 0 ? string.Empty : path[..idx];
    }

    private static byte[] SerializeKeyPage(KeyPage page)
    {
        var buffer = new byte[FileHeader.PageSize];
        page.Write(buffer);
        return buffer;
    }
}
