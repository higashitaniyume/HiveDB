using System.Buffers.Binary;

namespace HiveDB.Storage;

internal sealed class FileHeader
{
    public const uint MagicValue = 0x45564948; // "HIVE" in little-endian
    public const uint CurrentVersion = 1;
    public const int PageSize = 4096;
    private const int HeaderFieldSize = 24;

    public uint Magic { get; set; }
    public uint Version { get; set; }
    public uint StoredPageSize { get; set; }
    public int FreePageHead { get; set; }
    public int RootKeyPage { get; set; }
    public int TotalPageCount { get; set; }
    public EncryptionHeader? Encryption { get; set; }

    public static FileHeader CreateNew(int rootKeyPage, EncryptionHeader? encryption = null) => new()
    {
        Magic = MagicValue,
        Version = CurrentVersion,
        StoredPageSize = PageSize,
        FreePageHead = 0,
        RootKeyPage = rootKeyPage,
        TotalPageCount = 2,
        Encryption = encryption,
    };

    public static FileHeader Read(byte[] buffer)
    {
        var header = new FileHeader
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0)),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)),
            StoredPageSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)),
            FreePageHead = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(12)),
            RootKeyPage = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(16)),
            TotalPageCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(20)),
        };

        // Check basic validity first for better error messages
        if (header.Magic != MagicValue)
            throw new HiveDBException("Not a valid HiveDB file (bad magic).");
        if (header.Version != CurrentVersion)
            throw new HiveDBException(
                $"Unsupported file version: {header.Version}. Expected: {CurrentVersion}.");
        if (header.StoredPageSize != PageSize)
            throw new HiveDBException(
                $"Unsupported page size: {header.StoredPageSize}. Expected: {PageSize}.");

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(24));
        uint computedCrc = Crc32.Compute(buffer.AsSpan(0, HeaderFieldSize));
        if (storedCrc != computedCrc)
        {
            throw new HiveDBException(
                $"Header CRC mismatch: stored 0x{storedCrc:X8}, computed 0x{computedCrc:X8}");
        }

        // Read encryption metadata from the extended header area
        header.Encryption = EncryptionHeader.Read(buffer, EncryptionHeader.HeaderOffset);

        return header;
    }

    public void Write(byte[] buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), StoredPageSize);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), FreePageHead);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), RootKeyPage);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(20), TotalPageCount);

        uint crc = Crc32.Compute(buffer.AsSpan(0, HeaderFieldSize));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24), crc);

        if (Encryption != null)
        {
            Encryption.Write(buffer, EncryptionHeader.HeaderOffset);
            buffer.AsSpan(EncryptionHeader.HeaderOffset + EncryptionHeader.Size,
                PageSize - EncryptionHeader.HeaderOffset - EncryptionHeader.Size).Clear();
        }
        else
        {
            buffer.AsSpan(28, PageSize - 28).Clear();
        }
    }

    public void Validate()
    {
        if (Magic != MagicValue)
            throw new HiveDBException("Not a valid HiveDB file (bad magic).");
        if (Version != CurrentVersion)
            throw new HiveDBException(
                $"Unsupported file version: {Version}. Expected: {CurrentVersion}.");
        if (StoredPageSize != PageSize)
            throw new HiveDBException(
                $"Unsupported page size: {StoredPageSize}. Expected: {PageSize}.");
    }
}
