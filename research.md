# HiveDB: A Lightweight Embedded Key-Value Database with Authenticated Encryption

## Abstract

HiveDB is a single-file, page-based, embedded key-value database for .NET, inspired by the Windows Registry hive file format. It organizes data as a hierarchical tree of named keys, each containing typed values (String, DWord, QWord, Binary, MultiString). The storage engine uses fixed 4096-byte pages with CRC-32 header integrity and CRC-16 per-page integrity. The library targets `netstandard2.1` and `net8.0`, supports AES-256-GCM authenticated encryption and HMAC-SHA256 signing, an LRU page cache with 64-entry capacity, and a ReaderWriterLockSlim-based concurrency model for multiple readers with a single writer. A companion CLI provides 10 commands for database creation, CRUD operations, tree visualization, and benchmarking.

---

## 1. Introduction

Embedded databases fill the gap between flat configuration files and full client-server relational database systems. They provide structured, queryable storage without external processes or network dependencies. HiveDB is designed for applications that need hierarchical key-value storage with strong integrity guarantees, optional encryption, and minimal deployment footprint — a single DLL with zero external NuGet dependencies.

The project draws architectural inspiration from the Windows Registry hive format: a tree of keys identified by backslash-delimited paths (e.g., `Software\MyApp\Settings`), with named values of various types stored on each key. The on-disk format uses fixed-size 4096-byte pages with multiple page types (Header, Key, Overflow, Free), linked structures for sibling chains and overflow data, and CRC integrity checks at both the header and page level.

This paper describes the complete technical design of HiveDB version 1.0, including file format specifications, storage engine algorithms, cryptographic protection mechanisms, concurrency model, public API, and test coverage.

---

## 2. File Format

### 2.1 Page Architecture

All I/O operates on fixed-size pages of 4096 bytes (`FileHeader.PageSize`). The first byte of every page identifies its type via the `PageType` enumeration:

| Value | Name | Purpose |
|-------|------|---------|
| `0x00` | Free | Recycled page; forms a singly-linked free list |
| `0x01` | Header | Page 0 only; contains file metadata |
| `0x02` | Key | Tree node; stores key name, child/sibling pointers, and value entries |
| `0x03` | Overflow | Payload page for values exceeding inline space |

### 2.2 Page 0 — File Header

The header page occupies page 0 and is always unencrypted, even in protected databases. This ensures format detection and protection metadata can be read without a password.

| Offset | Size | Field | Encoding | Description |
|--------|------|-------|----------|-------------|
| 0x00 | 4 | Magic | uint32 LE | `0x45564948` — ASCII "HIVE" in little-endian |
| 0x04 | 4 | Version | uint32 LE | Current version: `1` |
| 0x08 | 4 | StoredPageSize | uint32 LE | Always `4096` |
| 0x0C | 4 | FreePageHead | int32 LE | Page number at head of free list; 0 if empty |
| 0x10 | 4 | RootKeyPage | int32 LE | Page number of root key; always `1` at creation |
| 0x14 | 4 | TotalPageCount | int32 LE | Total allocated pages in the file |
| 0x18 | 4 | HeaderCRC32 | uint32 LE | CRC-32 of bytes 0x00–0x17 (24 bytes) |
| 0x1C | 56 | EncryptionHeader | struct | Protection metadata (see §4.1) |
| 0x54 | 4012 | — | zeros | Remainder of page |

#### CRC-32

The CRC-32 is computed over the first 24 bytes of the header (the `HeaderFieldSize` constant). Polynomial: `0xEDB88320` (IEEE 802.3). Initial value: `0xFFFFFFFF`; final value XORed with `0xFFFFFFFF`. Implementation: `Storage/Crc32.cs`.

### 2.3 Data Page Layout (Unprotected)

All pages except page 0 share a common prefix:

| Offset | Size | Field |
|--------|------|-------|
| 0x00 | 1 | PageType byte |
| 0x01 | 1 | Flags byte (key pages: bit 0 = IsDeleted) |
| 0x02 | 2 | CRC-16 of bytes 0x04–0xFFF (4092 bytes) |
| 0x04 | 4092 | Type-specific payload |

#### CRC-16

Polynomial: `0x1021` (CRC-16-CCITT). Initial value: `0xFFFF`. Computed over bytes 4–4095 inclusive. Written in little-endian at bytes 2–3. Implementation: `Storage/Crc16.cs`.

### 2.4 Key Page (PageType = 0x02)

Each key page represents one node in the tree. The root key (page 1) has `KeyName = ""` (empty string).

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0x00 | 1 | PageType | `0x02` |
| 0x01 | 1 | Flags | bit 0 = IsDeleted |
| 0x02 | 2 | CRC-16 | Integrity check |
| 0x04 | 2 | KeyNameLen | UTF-8 byte length of key name (uint16 LE) |
| 0x06 | 4 | ParentPage | Page number of parent key (int32 LE, 0 for root) |
| 0x0A | 4 | FirstChildPage | Page number of first child (int32 LE, 0 if none) |
| 0x0E | 4 | NextSiblingPage | Page number of next sibling (int32 LE, 0 if last) |
| 0x12 | 2 | ValueCount | Number of value entries (uint16 LE) |
| 0x14 | N | KeyName | Key name in UTF-8 (N = KeyNameLen bytes) |
| 0x14+N | var | ValueEntries | Sequence of value entry structures |

The fixed header portion is 20 bytes (`FixedHeaderSize` constant). The key page's maximum writable area defaults to 4096 bytes for unprotected mode and 4068 bytes (`CryptoManager.MaxDataSize`) for protected mode (see §4.2).

#### Value Entry Structure

Each value entry within a key page:

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 2 | NameLen | UTF-8 byte length of value name (uint16 LE) |
| 2 | 2 | Kind | `RegistryValueKind` enum value (uint16 LE) |
| 4 | 4 | DataLength | Total data size in bytes (int32 LE) |
| 8 | 4 | OverflowPage | Page number of first overflow page (int32 LE, 0 = inline) |
| 12 | N | Name | Value name in UTF-8 (N bytes) |
| 12+N | M | InlineData | Raw data bytes (M = DataLength); only present when OverflowPage = 0 |

Per-entry header size: 12 bytes (`ValueEntryHeaderSize` constant).

### 2.5 Overflow Page (PageType = 0x03)

Values too large to fit inline are stored in a singly-linked overflow chain.

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0x00 | 1 | PageType | `0x03` |
| 0x01 | 1 | — | Unused |
| 0x02 | 2 | CRC-16 | Integrity check |
| 0x04 | 2 | DataLen | Bytes of payload in this page (uint16 LE) |
| 0x06 | 4 | NextPage | Next overflow page in chain (int32 LE, 0 = end) |
| 0x0A | M | Data | Payload data (M bytes, M ≤ maxPerPage) |

Maximum payload per overflow page (`maxPerPage`) is `(MaxDataSize - 10)`:
- Unprotected mode: `4096 - 10 = 4086` bytes
- Protected mode: `4068 - 10 = 4058` bytes

### 2.6 Free Page (PageType = 0x00)

Freed pages form a singly-linked LIFO list starting from `FreePageHead` in the header.

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0x00 | 1 | PageType | `0x00` |
| 0x01 | 1 | — | Unused |
| 0x02 | 2 | CRC-16 | Integrity check |
| 0x04 | 4 | NextFree | Next free page number (int32 LE, 0 = end of list) |

---

## 3. Tree Structure

### 3.1 Organization

Keys form a hierarchical tree. Each key occupies exactly one page. Children of a parent are organized as a **singly-linked list** via the `FirstChildPage` (parent) and `NextSiblingPage` (each child) pointers. The last sibling has `NextSiblingPage = 0`.

This is not a B-tree. There is no enforced ordering among siblings, no rebalancing, and no fan-out optimization. The tree is navigated by linear search through the sibling chain for each path segment.

### 3.2 Path Resolution

`TreeNavigator.ResolveKey(string path, bool createMissing)` at `Tree/TreeNavigator.cs:19`:

1. **Normalize**: Replace `/` with `\`, split on `\`, remove empty segments.
2. **Start** at `RootKeyPage` (page 1, key name `""`).
3. For each path segment:
   a. Read the current key page from disk (via `PageManager.ReadPage`).
   b. Walk the `FirstChildPage → NextSiblingPage` chain, comparing `KeyName` for each sibling.
   c. If a matching child is found, advance to it and continue to the next segment.
   d. If no match and `createMissing == false`, return `null`.
   e. If `createMissing == true`, allocate a new key page, set its `KeyName` to the segment, and append it to the end of the sibling chain (walk to the last sibling, set its `NextSiblingPage`, or set `Parent.FirstChildPage` if this is the first child).
4. Return the final key page.

### 3.3 Child Unlinking

`TreeNavigator.UnlinkChild(KeyPage parent, string name)` at line 152:

1. Call `FindChildWithPrev` to locate the target child and its preceding sibling.
2. If the target is the first child, update `parent.FirstChildPage = target.NextSiblingPage`.
3. Otherwise, update `prevSibling.NextSiblingPage = target.NextSiblingPage`.
4. This bypasses the target in the sibling chain without rewriting any data on the target page itself.

### 3.4 Available Space

`KeyPage.AvailableSpace` (line 29–38) computes free bytes on a key page:

```
AvailableSpace = MaxDataSize - FixedHeaderSize(20) - UTF8_ByteCount(KeyName) - SerializedValuesSize()
```

`SerializedValuesSize()` sums, per value entry:
- `ValueEntryHeaderSize(12) + UTF8_ByteCount(ValueName)`
- Plus `InlineData.Length` if `OverflowPage == 0`.

The `MaxDataSize` defaults to `FileHeader.PageSize` (4096) for unprotected databases and is set to `CryptoManager.MaxDataSize` (4068) for protected databases.

---

## 4. Cryptographic Protection

### 4.1 Protection Metadata

Stored in the 56-byte `EncryptionHeader` at offset 28 of page 0. The first 4 bytes are flags — bits 0–1 encode the `ProtectionMode`:

| Value | Mode | Description |
|-------|------|-------------|
| 0 | `None` | No protection; CRC-16 integrity only |
| 1 | `Encrypted` | AES-256-GCM authenticated encryption |
| 2 | `Signed` | HMAC-SHA256 authentication without encryption |

Layout within the 56-byte block (relative to offset 28 of page 0):

| Relative Offset | Size | Field |
|-----------------|------|-------|
| 0x00 | 4 | Flags (uint32 LE, bits 0–1 = ProtectionMode) |
| 0x04 | 16 | Salt (128-bit random) |
| 0x14 | 32 | KeyCheckHash (SHA-256) |
| 0x34 | 4 | Pbkdf2Iterations (int32 LE) |

### 4.2 Protected Page Layout

When protection is active (Encrypted or Signed), each data page reserves a 28-byte footer:

| Offset | Size | GCM Content | HMAC Content |
|--------|------|-------------|--------------|
| 0x000 | 1 | PageType (unencrypted; GCM associated data) | PageType (included in HMAC) |
| 0x001 | 4067 | GCM ciphertext | Plaintext (not encrypted) |
| 0xFE4 | 12 | Random nonce (96-bit) | Zeros |
| 0xFF0 | 16 | GCM authentication tag (128-bit) | Truncated HMAC-SHA256 (128-bit) |

Maximum data area: `PageSize - FooterSize = 4096 - 28 = 4068` bytes (`CryptoManager.MaxDataSize`).

### 4.3 Key Derivation

`CryptoManager.DeriveKey` at `Storage/CryptoManager.cs:47`:

```
key = PBKDF2-HMAC-SHA256(password_utf8, salt, iterations, outputLen=32)
```

| Parameter | Value |
|-----------|-------|
| Hash function | HMAC-SHA256 |
| Iterations | 600,000 (OWASP 2023 recommendation) |
| Output length | 32 bytes (256-bit) |
| Salt | 128-bit random (`RandomNumberGenerator`) |

### 4.4 Key Verification

`CryptoManager.ComputeKeyCheckHash` at line 67:

```
checkHash = SHA256(key || UTF8("HiveDB-KeyCheck-V1"))
```

This 32-byte hash is stored in the header and verified in constant time (`CryptographicOperations.FixedTimeEquals`) before any data decryption, providing early rejection of incorrect passwords without touching data pages.

### 4.5 AES-256-GCM Mode

**Platform requirement**: `net8.0` or later. Throws `PlatformNotSupportedException` on `netstandard2.1`.

**Encryption** (`EncryptPageGcm`, line 150):
1. Generate a fresh random 12-byte nonce via `RandomNumberGenerator.Fill`.
2. Extract 4067 bytes of plaintext from buffer offsets 1–4067.
3. Use byte 0 (PageType) as GCM associated data (authenticated but not encrypted).
4. Call `AesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData)`.
5. Write ciphertext back to offsets 1–4067.
6. Write nonce at offsets 4068–4079.
7. Write 16-byte tag at offsets 4080–4095.

**Decryption** (`DecryptPageGcm`, line 169):
1. Read nonce from offsets 4068–4079 and tag from offsets 4080–4095.
2. Read ciphertext from offsets 1–4067.
3. Call `AesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData)`.
4. On `CryptographicException`, throw `HiveDBException("Page authentication failed...")`.
5. Write plaintext back to offsets 1–4067.

**Nonce uniqueness**: Each page encryption generates a fresh random 96-bit nonce. With a birthday bound of 2^48 encryptions before collision risk, this is sufficient for any practical file size.

### 4.6 HMAC-SHA256 Mode

**Signing** (`SignPageHmac`, line 202):
1. Zero out bytes 4068–4095 (the footer area).
2. Compute `HMACSHA256(key, buffer[0..4095])` over the complete 4096-byte page.
3. Truncate the result to 16 bytes and store at offsets 4080–4095.

**Verification** (`VerifyPageHmac`, line 217):
1. Save the 16-byte stored HMAC from offsets 4080–4095.
2. Zero out the footer area.
3. Recompute HMAC-SHA256 over the full page.
4. Compare stored vs. recomputed using `CryptographicOperations.FixedTimeEquals`.
5. If mismatch, throw `HiveDBException`.
6. Restore the stored HMAC (making verification idempotent).

---

## 5. Storage Engine

### 5.1 BinaryFileHandle

`Storage/BinaryFileHandle.cs` wraps a `FileStream` opened with `FileOptions.RandomAccess` and a 4096-byte buffer. All public methods (`ReadPage`, `WritePage`, `SetLength`, `Flush`, `Dispose`, `Length`) serialize access via a `lock` object. This prevents interleaved I/O at the OS level.

Page addresses are computed as `pageNumber × 4096`. `ReadPage` seeks to the offset and reads exactly 4096 bytes. The custom `ReadExactly` method loops until the full page is read, throwing `EndOfStreamException` on early EOF.

### 5.2 PageCache

`Storage/PageCache.cs` implements a fixed-capacity LRU eviction policy:

| Property | Value |
|----------|-------|
| Capacity | 64 entries (256 KB when full) |
| Data structure | `Dictionary<int, LinkedListNode<CacheEntry>>` for O(1) lookup |
| Eviction order | `LinkedList<CacheEntry>` ordered by recency |
| Thread safety | All methods lock on a private object |

On `TryGet`: hit moves the node to front of LRU list. On `Put`: if at capacity, evicts the tail (least recently used); inserts at front. On `Invalidate`: removes entry from both dictionary and linked list.

### 5.3 PageManager — Page Allocation

`Storage/PageManager.cs:106` (`AllocatePage`):

1. Read header to check `FreePageHead`.
2. **If free list has pages**: pop the head page, read it to get the next pointer at bytes 4–7, update `FreePageHead`, and return the popped page.
3. **If free list is empty**: grow the file by `GrowthPages = 8` pages (32 KB):
   - Extend file length via `SetLength`.
   - Return the first new page as the allocation.
   - Chain the remaining 7 pages into the free list, each written as a `PageType.Free` page with CRC-16 via `WritePage`.
   - Update `TotalPageCount` and write the updated header.

The caller is responsible for writing the allocated page with full content; `AllocatePage` no longer writes the initial type byte (removed as a redundant write optimization).

### 5.4 Free List

`FreePage` at line 148:
1. Allocate a buffer, set `PageType.Free`, write the old `FreePageHead` at bytes 4–7.
2. Write page via `WritePage` (which computes CRC-16 and applies crypto if active).
3. Update header `FreePageHead` to the freed page number.
4. Write updated header.

The free list is LIFO — the most recently freed page is reused first. This provides temporal locality but no defragmentation.

### 5.5 Overflow Chain

**Write** (`WriteOverflowChain`, line 181): Splits data into chunks ≤ `maxPerPage`, allocates overflow pages, writes chunk + length + next-page pointer, and back-links each previous page's next pointer. Returns the first page number.

**Read** (`ReadOverflowChain`, line 161): Walks the linked chain, copying chunk data from offset 10 of each page into a contiguous result buffer.

**Free** (`FreeOverflowChain`, line 219): Walks the chain, calling `FreePage` on each.

---

## 6. Value Serialization

`Value/ValueSerializer.cs` handles all seven value types. Each type has a specific wire format:

### String (RegistryValueKind = 1) and ExpandString (2)
- **Serialize**: UTF-8 encode, append `0x00` (null terminator).
- **Deserialize**: Trim trailing null bytes from the data, then UTF-8 decode.

### DWord (4)
- **Serialize**: 4 bytes, little-endian (native platform order on x86/x64 but explicitly LE via bit shifts).
- **Deserialize**: Reconstruct int32 from 4 LE bytes via bit shifts: `b[0] | (b[1]<<8) | (b[2]<<16) | (b[3]<<24)`.

### QWord (11)
- **Serialize**: 8 bytes, little-endian via bit shifts.
- **Deserialize**: Reconstruct int64 from 8 LE bytes.

### Binary (3)
- Direct byte array passthrough — no encoding overhead.

### MultiString (7)
- **Serialize**: Each string is UTF-8 encoded and null-terminated individually. A final `0x00` byte terminates the entire sequence. Example: `["a","b"]` → `61 00 62 00 00`.
- **Deserialize**: Split on null bytes; a pair of consecutive nulls (i.e., an empty segment) signals the end. Empty arrays produce zero strings.

### None (0)
- Raw byte array with no type interpretation.

### Type Inference

`RegistryKey.SetValue(name, value)` without an explicit kind infers the type from the CLR type:
- `string` → `String`
- `int` → `DWord`
- `long` → `QWord`
- `byte[]` → `Binary`
- `string[]` → `MultiString`

Other types throw `ArgumentException`.

---

## 7. Concurrency Model

Two independent locking layers operate simultaneously:

### 7.1 File-Level Lock

`BinaryFileHandle` uses `lock (_lock)` on all operations. This serializes all raw file I/O (read, write, seek, flush).

### 7.2 Logical Read/Write Lock

`PageManager` uses a `ReaderWriterLockSlim` with `LockRecursionPolicy.SupportsRecursion`:

```
Read operations  → EnterReadLock / ExitReadLock
Write operations → EnterWriteLock / ExitWriteLock
```

Each public API method acquires the appropriate lock via `using var rl = _pages.ReadLock()` or `using var wl = _pages.WriteLock()`. The `LockScope` disposable struct calls the corresponding exit method on dispose.

| Lock Type | Operations |
|-----------|-----------|
| **ReadLock** | `GetValue`, `GetValueKind`, `KeyExists`, `GetSubKeyNames`, `GetValueNames`, `ParentKey`, `OpenSubKey`, `HasSubKey` |
| **WriteLock** | `CreateKey`, `DeleteKey`, `SetValue`, `DeleteValue`, `CreateSubKey`, `DeleteSubKey` |

Recursive locking within the same thread is permitted. For example, `RegistryDatabase.DeleteKey` acquires a write lock, then calls `RegistryKey.DeleteSubKey` which acquires its own write lock — both succeed.

### 7.3 Concurrency Properties

| Property | Behavior |
|----------|----------|
| Multiple concurrent readers | Supported; no mutual blocking |
| Reader during write | Blocked until write completes |
| Writer during write | Blocked; exclusive write access |
| File-level sharing | Write: `FileShare.None`; Read-only: `FileShare.Read` |
| Multi-process access | Read-only: multiple processes; Write: single process |

---

## 8. Public API

### 8.1 RegistryDatabase

```csharp
public sealed class RegistryDatabase : IDisposable
```

| Method | Description |
|--------|-------------|
| `static Create(filePath, password?, signOnly?)` | Create a new database file |
| `static Open(filePath, readOnly?, password?)` | Open an existing database |
| `RootKey` | Get the root `RegistryKey` |
| `IsReadOnly` | Whether the database was opened read-only |
| `FileSize` | Current file length in bytes |
| `GetValue(path, name, defaultValue?)` | Read a value at a path |
| `SetValue(path, name, value, kind)` | Write a value at a path |
| `GetValueKind(path, name)` | Get the `RegistryValueKind` of a value |
| `CreateKey(path)` | Create a key (with all intermediate segments) |
| `DeleteKey(path, recursive?)` | Delete a key; `recursive` removes children |
| `KeyExists(path)` | Check if a key exists |
| `GetSubKeyNames(path)` | List child key names |
| `GetValueNames(path)` | List value names on a key |
| `DeleteValue(path, name)` | Remove a value from a key |
| `Flush()` | Force-write buffered data to disk |
| `Dispose()` | Close the file and release all resources |

### 8.2 RegistryKey

```csharp
public sealed class RegistryKey : IDisposable
```

| Member | Description |
|--------|-------------|
| `Name` | The leaf name of this key |
| `FullPath` | Full backslash-delimited path from root |
| `Database` | The owning `RegistryDatabase` |
| `ParentKey` | The parent key (null for root) |
| `CreateSubKey(name)` | Create a child key (supports nested paths) |
| `OpenSubKey(name, writable?)` | Open an existing child key |
| `DeleteSubKey(name, recursive?)` | Delete a child key |
| `GetSubKeyNames()` | List child key names |
| `HasSubKey(name)` | Check if a child exists |
| `SetValue(name, value)` | Set a value with auto type inference |
| `SetValue(name, value, kind)` | Set a value with explicit type |
| `GetValue(name, defaultValue?)` | Get a value |
| `GetValue<T>(name, defaultValue?)` | Get a value with generic type (extension) |
| `GetValueKind(name)` | Get the type of a value |
| `DeleteValue(name)` | Remove a value |
| `GetValueNames()` | List value names |
| `Dispose()` | Release the key handle |

---

## 9. Command-Line Interface

The CLI tool (`src/HiveDB.Cli`) uses `System.CommandLine` 2.0.8 with Chinese-localized help text. Ten commands are registered:

| Command | Alias | Options | Description |
|---------|-------|---------|-------------|
| `create` | — | `-p`, `-s` | Create a new database |
| `set` | — | `-k`, `-p` | Set a value |
| `get` | — | `-p` | Get a value |
| `enum` | — | `-p` | List sub-keys and values |
| `delete-key` | `rmkey` | `-r`, `-p` | Delete a key |
| `delete-value` | `rmval` | `-p` | Delete a value |
| `info` | — | `-p` | Show database metadata |
| `tree` | — | `-p` | Tree visualization |
| `test` | — | `-p`, `-s` | Run functional test suite |
| `bench` | — | `-n`, `-p`, `-s` | Performance benchmark |

Shared options: `--password` (`-p`), `--sign` (`-s`), `--kind` (`-k`), `--recursive` (`-r`), `--count` (`-n`).

---

## 10. Test Coverage

The test project (`test/HiveDB.Tests`) contains **120 xUnit tests** across 9 test classes:

| Test Class | Tests | Coverage |
|------------|-------|----------|
| `RegistryDatabaseTests` | 27 | Full CRUD, dispose, edge cases, file size growth |
| `RegistryKeyTests` | 15 | Tree navigation, type inference, parent/child, overflow |
| `ConcurrencyTests` | 2 | 4-reader non-blocking, 4-writer stress test |
| `CryptoPageTests` | 25 | GCM encryption, HMAC signing, tamper detection, password validation, backward compat |
| `CorruptionTests` | 3 | Truncated file, bad magic, CRC mismatch |
| `KeyPageTests` | 10 | Serialization round-trip, flags, space checks, overflow |
| `TreeNavigatorTests` | 7 | Path resolution, create-missing, siblings, slash normalization |
| `ValueSerializerTests` | 11 | All 7 value kinds, null trimming, edge cases |
| `PageManagerTests` | 6 | Allocation, free list reuse, overflow chain round-trip |

The `FolderScanner` utility in `test/FolderScanner` is a standalone console tool (not xUnit) that recursively scans a directory tree and stores file metadata in a HiveDB database.

---

## 11. Project Structure

```
HiveDB.sln
├── src/
│   ├── HiveDB/                          netstandard2.1 + net8.0 class library
│   │   ├── RegistryDatabase.cs          Public API: factory, CRUD, path-based operations
│   │   ├── RegistryKey.cs               Key node with sub-key and value operations
│   │   ├── RegistryKeyExtensions.cs     Generic GetValue<T> with numeric coercion
│   │   ├── RegistryValueKind.cs         Value type enumeration (7 kinds)
│   │   ├── HiveDBException.cs           Custom IOException subclass
│   │   ├── Polyfills.cs                 IsExternalInit polyfill for netstandard2.1
│   │   ├── Storage/
│   │   │   ├── BinaryFileHandle.cs      FileStream wrapper with per-operation locking
│   │   │   ├── Crc16.cs                 CRC-16-CCITT (polynomial 0x1021)
│   │   │   ├── Crc32.cs                 CRC-32 (polynomial 0xEDB88320)
│   │   │   ├── CryptoManager.cs         PBKDF2, AES-256-GCM, HMAC-SHA256
│   │   │   ├── EncryptionHeader.cs      Protection metadata (56-byte struct)
│   │   │   ├── FileHeader.cs            Page-0 format: magic, version, CRC-32
│   │   │   ├── PageCache.cs             LRU cache (capacity 64, ~256 KB)
│   │   │   ├── PageManager.cs           Page alloc/free, overflow chains, CRC, crypto
│   │   │   └── PageType.cs              Enum: Free/Header/Key/Overflow
│   │   ├── Tree/
│   │   │   ├── KeyPage.cs               Key page serialization, ValueEntry, inline/overflow
│   │   │   └── TreeNavigator.cs         Path resolution, sibling chain traversal
│   │   └── Value/
│   │       └── ValueSerializer.cs       Wire format for all 7 RegistryValueKind types
│   └── HiveDB.Cli/                      net9.0 console app
│       └── Program.cs                   10 commands via System.CommandLine
├── test/
│   ├── HiveDB.Tests/                    net8.0 xUnit (120 tests, 9 classes)
│   └── FolderScanner/                   net8.0 console utility
└── readme.md
```

---

## 12. Key Design Decisions

### 12.1 Fixed-Size Pages

All pages are exactly 4096 bytes. This provides O(1) random access via `page_number × 4096` and simplifies the I/O layer. The trade-off is internal fragmentation: most key pages use only a few hundred bytes, wasting the remaining space.

### 12.2 Singly-Linked Sibling Chains

Children are organized as linked lists rather than arrays. This simplifies insertion (always append) and deletion (patch one pointer). The cost is O(n) lookup within sibling chains. For typical configuration databases with few hundred keys, this is acceptable.

### 12.3 GCM Rather Than Separate Encryption + MAC

Using AES-256-GCM provides both confidentiality and authentication in a single cryptographic primitive, avoiding the risks of compose-your-own schemes (e.g., MAC-then-encrypt vs. encrypt-then-MAC ordering). The 28-byte per-page overhead (12 nonce + 16 tag) reduces usable page space by 0.7%.

### 12.4 Deterministic Key Verification

The key check hash (SHA-256 of derived key + context string) allows rejecting incorrect passwords without attempting decryption of any data page. This avoids the performance cost and potential error confusion of GCM tag verification failures on password entry.

### 12.5 PBKDF2 Iteration Count

The 600,000 iteration count follows OWASP 2023 recommendations for HMAC-SHA256. On modern hardware, key derivation takes approximately 150–300 ms, which is a one-time cost at database open.

### 12.6 Growth Batch of 8 Pages

File growth by 8 pages (32 KB) balances the trade-off between frequent file extension system calls (smaller batches) and wasted disk space (larger batches). Unused pages from a growth batch are immediately chained into the free list.

---

## 13. Limitations

1. **Coarse-grained locking**: A single `ReaderWriterLockSlim` for the entire database. A write to any key blocks reads to all keys.
2. **No transactions**: Each operation is independent. No atomic multi-operation batches, no rollback.
3. **No WAL / journal**: A crash during a write may leave the file in an inconsistent state.
4. **No B-tree / index**: Sibling chains are linear lists; lookup is O(n) per level.
5. **Single-writer file access**: `FileShare.None` for writable opens prevents multi-process writers.
6. **Internal fragmentation**: Fixed 4096-byte pages waste space for databases with many small keys.
7. **No online compaction**: Deleted pages are recycled but never compacted; file size never shrinks.

---

## References

- NIST SP 800-38D: Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)
- RFC 2898: PKCS #5: Password-Based Cryptography Specification Version 2.0
- OWASP Password Storage Cheat Sheet (2023)
- Windows Registry Hive Format (Microsoft Win32 API documentation)
- CRC-16-CCITT: ITU-T Recommendation X.25
- CRC-32: IEEE 802.3
