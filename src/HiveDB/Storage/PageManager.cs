using System.Buffers.Binary;

namespace HiveDB.Storage;

internal sealed class PageManager : IDisposable
{
    private readonly BinaryFileHandle _file;
    private readonly PageCache _cache;
    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.SupportsRecursion);
    private const int GrowthPages = 8;
    private CryptoManager? _crypto;

    internal CryptoManager? CryptoManager
    {
        get => _crypto;
        set => _crypto = value;
    }

    public PageManager(BinaryFileHandle file, PageCache cache, CryptoManager? crypto = null)
    {
        _file = file;
        _cache = cache;
        _crypto = crypto;
    }

    public bool IsReadOnly => _file.IsReadOnly;

    // ── Locking ──────────────────────────────────────────

    public IDisposable ReadLock() => LockScope.EnterRead(_rwLock);
    public IDisposable WriteLock() => LockScope.EnterWrite(_rwLock);

    // ── Header ───────────────────────────────────────────

    public FileHeader ReadHeader()
    {
        var buffer = new byte[FileHeader.PageSize];
        _file.ReadPage(0, buffer);
        return FileHeader.Read(buffer);
    }

    public void WriteHeader(FileHeader header)
    {
        var buffer = new byte[FileHeader.PageSize];
        header.Write(buffer);
        _file.WritePage(0, buffer);
    }

    // ── Raw page I/O ─────────────────────────────────────

    public byte[] ReadPage(int pageNumber)
    {
        if (_cache.TryGet(pageNumber, out var cached))
            return cached;

        var buffer = new byte[FileHeader.PageSize];
        _file.ReadPage(pageNumber, buffer);

        if (pageNumber != 0)
        {
            if (_crypto != null)
                _crypto.UnprotectPage(buffer);
            else
                ValidatePageCrc(buffer, pageNumber);
        }

        _cache.Put(pageNumber, buffer);
        return buffer;
    }

    public void WritePage(int pageNumber, byte[] buffer)
    {
        if (pageNumber != 0)
        {
            if (_crypto != null)
                _crypto.ProtectPage(buffer);
            else
                WritePageCrc(buffer);
        }

        _file.WritePage(pageNumber, buffer);
        _cache.Invalidate(pageNumber);
    }

    // ── Page integrity ───────────────────────────────────

    private static void ValidatePageCrc(byte[] buffer, int pageNumber)
    {
        ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(2));
        ushort computed = Crc16.Compute(buffer.AsSpan(4, FileHeader.PageSize - 4));
        if (stored != computed)
        {
            throw new HiveDBException(
                $"Page {pageNumber} CRC mismatch: stored 0x{stored:X4}, computed 0x{computed:X4}");
        }
    }

    private static void WritePageCrc(byte[] buffer)
    {
        ushort crc = Crc16.Compute(buffer.AsSpan(4, FileHeader.PageSize - 4));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), crc);
    }

    // ── Page allocation ──────────────────────────────────

    public int AllocatePage(PageType type)
    {
        var header = ReadHeader();
        int page;

        if (header.FreePageHead != 0)
        {
            // Reuse from free list
            page = header.FreePageHead;
            var freeBuffer = ReadPage(header.FreePageHead);
            int nextFree = BinaryPrimitives.ReadInt32LittleEndian(freeBuffer.AsSpan(4));
            header.FreePageHead = nextFree;
            WriteHeader(header);
        }
        else
        {
            // Grow the file by GrowthPages, allocate the first, chain the rest
            int firstNew = header.TotalPageCount;
            int lastNew = firstNew + GrowthPages - 1;
            long newLength = (firstNew + GrowthPages) * (long)FileHeader.PageSize;
            _file.SetLength(newLength);

            page = firstNew;

            // Chain unused pages into the free list
            for (int i = firstNew + 1; i <= lastNew; i++)
            {
                var freeBuffer = new byte[FileHeader.PageSize];
                freeBuffer[0] = (byte)PageType.Free;
                int next = (i < lastNew) ? (i + 1) : header.FreePageHead;
                BinaryPrimitives.WriteInt32LittleEndian(freeBuffer.AsSpan(4), next);
                WritePage(i, freeBuffer);
            }
            header.FreePageHead = firstNew + 1;
            header.TotalPageCount = firstNew + GrowthPages;
            WriteHeader(header);
        }

        return page;
    }

    public void FreePage(int pageNumber)
    {
        var header = ReadHeader();
        var buffer = new byte[FileHeader.PageSize];
        buffer[0] = (byte)PageType.Free;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), header.FreePageHead);
        WritePage(pageNumber, buffer);

        header.FreePageHead = pageNumber;
        WriteHeader(header);
    }

    // ── Overflow chain ───────────────────────────────────

    public byte[] ReadOverflowChain(int firstPage, int totalLength)
    {
        var result = new byte[totalLength];
        int offset = 0;
        int page = firstPage;

        while (page != 0 && offset < totalLength)
        {
            var buffer = ReadPage(page);
            ushort dataLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4));
            int nextPage = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(6));
            int toCopy = Math.Min(dataLen, totalLength - offset);
            Array.Copy(buffer, 10, result, offset, toCopy);
            offset += toCopy;
            page = nextPage;
        }

        return result;
    }

    public int WriteOverflowChain(byte[] data, int offset, int length)
    {
        int firstPage = 0;
        int prevPage = 0;
        int remaining = length;
        int dataOffset = offset;
        int maxPerPage = (_crypto != null ? CryptoManager.MaxDataSize : FileHeader.PageSize) - 10;

        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, maxPerPage);
            int currentPage = AllocatePage(PageType.Overflow);

            if (firstPage == 0)
                firstPage = currentPage;

            var buffer = new byte[FileHeader.PageSize];
            buffer[0] = (byte)PageType.Overflow;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)chunk);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(6), 0);
            Array.Copy(data, dataOffset, buffer, 10, chunk);
            WritePage(currentPage, buffer);

            if (prevPage != 0)
            {
                var prevBuffer = ReadPage(prevPage);
                BinaryPrimitives.WriteInt32LittleEndian(prevBuffer.AsSpan(6), currentPage);
                WritePage(prevPage, prevBuffer);
            }

            prevPage = currentPage;
            dataOffset += chunk;
            remaining -= chunk;
        }

        return firstPage;
    }

    public void FreeOverflowChain(int firstPage)
    {
        int page = firstPage;
        while (page != 0)
        {
            var buffer = ReadPage(page);
            int nextPage = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(6));
            FreePage(page);
            page = nextPage;
        }
    }

    // ── Properties ───────────────────────────────────────

    internal int TotalPageCount
    {
        get
        {
            var header = ReadHeader();
            return header.TotalPageCount;
        }
    }

    internal int FreePageCount
    {
        get
        {
            int count = 0;
            int page = ReadHeader().FreePageHead;
            while (page != 0)
            {
                count++;
                var buffer = ReadPage(page);
                page = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
            }
            return count;
        }
    }

    public void Dispose()
    {
        _file.Dispose();
    }

    // ── Lock scope helper ────────────────────────────────

    private sealed class LockScope : IDisposable
    {
        private ReaderWriterLockSlim? _lock;
        private readonly bool _isWrite;

        private LockScope(ReaderWriterLockSlim l, bool isWrite)
        {
            _lock = l;
            _isWrite = isWrite;
        }

        public static LockScope EnterRead(ReaderWriterLockSlim l)
        {
            l.EnterReadLock();
            return new LockScope(l, false);
        }

        public static LockScope EnterWrite(ReaderWriterLockSlim l)
        {
            l.EnterWriteLock();
            return new LockScope(l, true);
        }

        public void Dispose()
        {
            if (_lock == null) return;
            if (_isWrite) _lock.ExitWriteLock();
            else _lock.ExitReadLock();
            _lock = null;
        }
    }
}
