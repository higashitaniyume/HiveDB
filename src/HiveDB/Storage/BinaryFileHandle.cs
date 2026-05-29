using System.IO;

namespace HiveDB.Storage;

internal sealed class BinaryFileHandle : IDisposable
{
    private readonly FileStream _stream;
    private readonly bool _readOnly;
    private readonly object _lock = new();

    public BinaryFileHandle(string filePath, FileMode mode, FileAccess access, FileShare share)
    {
        _readOnly = access == FileAccess.Read;
        _stream = new FileStream(
            filePath,
            mode,
            access,
            share,
            FileHeader.PageSize,
            FileOptions.RandomAccess);
    }

    public long Length
    {
        get { lock (_lock) return _stream.Length; }
    }

    public bool IsReadOnly => _readOnly;

    public void ReadPage(int pageNumber, byte[] buffer)
    {
        long offset = (long)pageNumber * FileHeader.PageSize;
        lock (_lock)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            ReadExactly(_stream, buffer, 0, FileHeader.PageSize);
        }
    }

    public void WritePage(int pageNumber, byte[] buffer)
    {
        long offset = (long)pageNumber * FileHeader.PageSize;
        lock (_lock)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.Write(buffer, 0, FileHeader.PageSize);
        }
    }

    public void SetLength(long length)
    {
        lock (_lock) _stream.SetLength(length);
    }

    public void Flush()
    {
        lock (_lock) _stream.Flush();
    }

    public void Dispose()
    {
        lock (_lock) _stream.Dispose();
    }

    private static void ReadExactly(FileStream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
                throw new EndOfStreamException(
                    $"Expected {count} bytes but read only {totalRead}.");
            totalRead += read;
        }
    }
}
