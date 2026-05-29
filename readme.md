# HiveDB

A lightweight, single-file embedded key-value database for .NET, inspired by the Windows Registry hive file format.

## Overview

HiveDB stores hierarchical key-value data in a single `.db` file with a fixed 4096-byte page structure. It supports seven value types, transparent AES-256-GCM encryption, HMAC-SHA256 signing, CRC integrity checks, and a full-featured CLI.

### Use Cases

- Desktop application configuration storage
- Embedded system data persistence
- Hierarchical settings (registry-style key paths like `Software\MyApp\Settings`)
- Projects that need a simple embedded database without SQLite or external dependencies

### Key Features

- **Single-file storage** — all data in one `.db` file
- **Hierarchical key tree** — keys organized as `Software\MyApp\Settings`, supporting parent-child nesting
- **7 value types** — String, DWord (int32), QWord (int64), Binary, MultiString, ExpandString, None
- **AES-256-GCM encryption** — authenticated encryption per page (net8.0+)
- **HMAC-SHA256 signing** — integrity protection without encryption (all targets)
- **CRC integrity** — CRC-32 on file header, CRC-16 on each data page
- **LRU page cache** — 64-page (256 KB) cache for fast access
- **Free page recycling** — deleted pages are reused automatically
- **Overflow chains** — large values span multiple pages via linked overflow chains
- **Concurrent-safe** — `ReaderWriterLockSlim` supports multiple readers with a single writer
- **Zero external dependencies** — core library depends only on the .NET BCL
- **Multi-target** — `netstandard2.1` (HMAC) + `net8.0` (full GCM + HMAC)

---

## Framework Support

| Target | Encryption | Signing | Compatible Runtimes |
|--------|-----------|---------|-------------------|
| `net8.0` | AES-256-GCM | HMAC-SHA256 | .NET 8, 9, 10+ |
| `netstandard2.1` | — | HMAC-SHA256 | .NET Core 3.0+, .NET 5+, Mono 6.4+, Xamarin, Unity 2021.2+ |

---

## File Format

Fixed 4096-byte pages. The magic number is `48 49 56 45` — ASCII `HIVE` in little-endian.

### Page 0 — File Header

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| `0x00` | 4 | Magic | `HIVE` |
| `0x04` | 4 | Version | `1` |
| `0x08` | 4 | PageSize | `4096` |
| `0x0C` | 4 | FreePageHead | Free list head page number |
| `0x10` | 4 | RootKeyPage | Root key page (always `1`) |
| `0x14` | 4 | TotalPageCount | Total allocated pages |
| `0x18` | 4 | CRC32 | CRC-32 of bytes 0–23 |
| `0x1C` | 56 | EncHeader | Protection metadata (mode, salt, key check, iterations) |

### Page Types

| Value | Name | Purpose |
|-------|------|---------|
| `0x00` | Free | Free page (allocator free list) |
| `0x01` | Header | Page 0 (file header) |
| `0x02` | Key | Key page (tree node with values) |
| `0x03` | Overflow | Overflow data page (large value chain) |

### Key Page Layout

| Field | Size | Description |
|-------|------|-------------|
| PageType | 1 | `0x02` |
| Flags | 1 | bit 0 = IsDeleted |
| CRC16 | 2 | CRC-16 of bytes 4–4095 |
| KeyNameLen | 2 | Key name UTF-8 length |
| ParentPage | 4 | Parent page number |
| FirstChildPage | 4 | First child page (linked list head) |
| NextSiblingPage | 4 | Next sibling page (linked list) |
| ValueCount | 2 | Number of value entries |
| KeyName | variable | Key name (UTF-8) |
| ValueEntries | variable | Value entry sequence |

### Protected Page Layout (GCM / HMAC)

When protection is active, each data page reserves a 28-byte footer:

| Offset | Size | Content |
|--------|------|---------|
| `0x000` | 1 | PageType (unencrypted, GCM associated data) |
| `0x001` | 4067 | Payload (encrypted for GCM, plain for HMAC) |
| `0xFE4` | 12 | Nonce (GCM) / zeros (HMAC) |
| `0xFF0` | 16 | Auth tag (GCM) / truncated HMAC-SHA256 |

---

## API Reference

### Creating and Opening

```csharp
// Create a new database
using var db = RegistryDatabase.Create("config.db");

// Create with AES-256-GCM encryption
using var db = RegistryDatabase.Create("config.db", password: "mysecret");

// Create with HMAC signing only (no encryption)
using var db = RegistryDatabase.Create("config.db", password: "mysecret", signOnly: true);

// Open (read-only)
using var db = RegistryDatabase.Open("config.db", readOnly: true);

// Open with password
using var db = RegistryDatabase.Open("config.db", password: "mysecret");
```

### Key Operations

```csharp
// Create keys (auto-creates intermediate paths)
db.CreateKey(@"Software\MyApp\Settings");

// Check existence
bool exists = db.KeyExists(@"Software\MyApp\Settings");

// List sub-keys
string[] subKeys = db.GetSubKeyNames(@"Software\MyApp");

// Delete (recursive to remove children)
db.DeleteKey(@"Software\MyApp", recursive: true);
```

### Value Operations

```csharp
// Write values
db.SetValue(@"Software\MyApp\Settings", "Theme", "dark", RegistryValueKind.String);
db.SetValue(@"Software\MyApp\Settings", "MaxItems", 100, RegistryValueKind.DWord);
db.SetValue(@"Software\MyApp\Settings", "MaxSize", 1024L * 1024 * 1024, RegistryValueKind.QWord);
db.SetValue(@"Software\MyApp\Settings", "Thumbnail", new byte[] { 0xFF, 0xD8 }, RegistryValueKind.Binary);
db.SetValue(@"Software\MyApp", "Hosts", new[] { "localhost", "127.0.0.1" }, RegistryValueKind.MultiString);

// Read values
object? theme = db.GetValue(@"Software\MyApp\Settings", "Theme");   // "dark"
object? count = db.GetValue(@"Software\MyApp\Settings", "MaxItems"); // 100

// Get value kind
RegistryValueKind kind = db.GetValueKind(@"Software\MyApp\Settings", "MaxSize"); // QWord

// List value names
string[] names = db.GetValueNames(@"Software\MyApp\Settings");

// Delete a value
db.DeleteValue(@"Software\MyApp\Settings", "Thumbnail");
```

### RegistryKey Object Model

```csharp
using var key = db.RootKey.CreateSubKey(@"Software\MyApp");

// Implicit type inference
key.SetValue("Theme", "dark");          // → String
key.SetValue("Count", 42);              // → DWord
key.SetValue("MaxSize", 100L * 1024);   // → QWord

// Generic read
string? theme = key.GetValue<string>("Theme");
int? count = key.GetValue<int>("Count");

// Enumerate children
foreach (var subKeyName in key.GetSubKeyNames())
    Console.WriteLine(subKeyName);
```

### Supported Value Types

| Enum | C# Type | Description |
|------|---------|-------------|
| `String` | `string` | UTF-8 string, null-terminated |
| `DWord` | `int` | 32-bit signed integer |
| `QWord` | `long` | 64-bit signed integer |
| `Binary` | `byte[]` | Raw byte array |
| `MultiString` | `string[]` | Array of null-terminated strings |
| `ExpandString` | `string` | Expandable string (same serialization as String) |
| `None` | `byte[]` | Raw binary with no type hint |

---

## CLI Tool

```
Usage:
  HiveDB.CLI [command] [options]

Commands:
  create       <file>                          Create a new HiveDB database file
  set          <file> <path> <name> <value>    Set a value in the database
  get          <file> <path> <name>            Get a value from the database
  enum         <file> <path>                   List sub-keys and values at a path
  delete-key   <file> <path>                   Delete a key (alias: rmkey)
  delete-value <file> <path> <name>            Delete a value (alias: rmval)
  info         <file>                          Display database metadata
  test         <file>                          Run functional test suite
  bench        <file>                          Run performance benchmark

Options:
  -p, --password    Database password (for encryption/signing)
  -s, --sign        HMAC signing only (no encryption)
  -k, --kind        Value type: auto|string|dword|qword|binary|multi|hex
  -r, --recursive   Delete key and all sub-keys
  -n, --count       Benchmark iteration count
```

### CLI Examples

```bash
# Create a plain database
HiveDB.CLI create myapp.db

# Create with GCM encryption
HiveDB.CLI create myapp.db -p "mysecret"

# Create with HMAC signing only
HiveDB.CLI create myapp.db -p "mysecret" --sign

# Write values
HiveDB.CLI set myapp.db "App\\UI" Theme dark -p "mysecret"
HiveDB.CLI set myapp.db "App\\UI" FontSize 14 -k int -p "mysecret"

# Read values
HiveDB.CLI get myapp.db "App\\UI" Theme -p "mysecret"   # → dark

# Browse data
HiveDB.CLI enum myapp.db "App\\UI"
# Key: App\UI
# ------------------------------------------------------------
# Values:
#   FontSize = 14  (DWord)
#   Theme = dark  (String)

# Show metadata
HiveDB.CLI info myapp.db
# File:        myapp.db
# Size:        136.0 KB
# Pages:       34
# Read-only:   True
# Keys:        2
# Values:      2

# Run tests and benchmarks
HiveDB.CLI test test.db
HiveDB.CLI bench bench.db -n 1000
```

---

## Project Structure

```
├── src/
│   ├── HiveDB/                       # Core library (netstandard2.1 + net8.0)
│   │   ├── HiveDBException.cs        # Custom exceptions
│   │   ├── Polyfills.cs              # IsExternalInit polyfill for netstandard2.1
│   │   ├── RegistryDatabase.cs       # Public API (factory + path-based operations)
│   │   ├── RegistryKey.cs            # Key node API
│   │   ├── RegistryKeyExtensions.cs  # GetValue<T> generic extension
│   │   ├── RegistryValueKind.cs      # Value type enum
│   │   ├── Storage/
│   │   │   ├── BinaryFileHandle.cs   # Low-level file I/O with locking
│   │   │   ├── Crc16.cs              # CRC-16 (polynomial 0x1021)
│   │   │   ├── Crc32.cs              # CRC-32 (polynomial 0xEDB88320)
│   │   │   ├── CryptoManager.cs      # AES-256-GCM + HMAC-SHA256 engine
│   │   │   ├── EncryptionHeader.cs   # Protection metadata in file header
│   │   │   ├── FileHeader.cs         # Page 0 header (magic, version, CRC-32)
│   │   │   ├── PageCache.cs          # LRU page cache (64 pages)
│   │   │   ├── PageManager.cs        # Page alloc/free, overflow chains, CRC, crypto
│   │   │   └── PageType.cs           # Page type enum
│   │   ├── Tree/
│   │   │   ├── KeyPage.cs            # Key page serialization + value entry management
│   │   │   └── TreeNavigator.cs      # Path resolution and tree traversal
│   │   └── Value/
│   │       └── ValueSerializer.cs    # Serialization for all 7 value types
│   └── HiveDB.Cli/                   # CLI tool (net8.0)
│       └── Program.cs                # 9 commands with full implementation
├── test/
│   └── HiveDB.Tests/                 # xUnit test project (120 tests)
│       ├── ConcurrencyTests.cs       # Multi-threaded read/write stress tests
│       ├── CorruptionTests.cs        # Tampered file, CRC mismatch, truncated file
│       ├── CryptoPageTests.cs        # GCM encryption, HMAC signing, tamper detection
│       ├── KeyPageTests.cs           # Key page serialization round-trips
│       ├── PageManagerTests.cs       # Page allocation, overflow chains, free list
│       ├── RegistryDatabaseTests.cs  # Full CRUD, dispose, edge cases
│       ├── RegistryKeyTests.cs       # Tree navigation, parent/child, generic GetValue
│       ├── TreeNavigatorTests.cs     # Path resolution, create-missing, subtree walk
│       └── ValueSerializerTests.cs   # Serialize/deserialize for all value types
├── HiveDB.sln
└── readme.md
```

---

## Build & Test

```bash
# Build the solution
dotnet build HiveDB.sln

# Run all 120 tests
dotnet test HiveDB.sln

# Build and run the CLI
dotnet run --project src/HiveDB.Cli -- --help
```

## License

This project is for educational and research purposes.
