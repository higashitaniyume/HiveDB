using System.Buffers.Binary;
using System.Text;
using HiveDB.Storage;

namespace HiveDB.Tree;

internal sealed class ValueEntry
{
    public string Name { get; init; } = string.Empty;
    public RegistryValueKind Kind { get; init; }
    public int DataLength { get; init; }
    public int OverflowPage { get; init; }
    public byte[] InlineData { get; init; } = Array.Empty<byte>();
}

internal sealed class KeyPage
{
    private const int FixedHeaderSize = 20;
    private const int ValueEntryHeaderSize = 12;

    /// <summary>
    /// Maximum data bytes available in a page. Defaults to PageSize.
    /// Set to <see cref="CryptoManager.MaxPayloadSize"/> for protected databases.
    /// </summary>
    internal static int MaxDataSize { get; set; } = FileHeader.PageSize;

    public int PageNumber { get; set; }
    public bool IsDeleted { get; set; }
    public string KeyName { get; set; } = string.Empty;
    public int ParentPage { get; set; }
    public int FirstChildPage { get; set; }
    public int NextSiblingPage { get; set; }
    public List<ValueEntry> Values { get; private set; } = new();

    public int AvailableSpace
    {
        get
        {
            int used = FixedHeaderSize
                + Encoding.UTF8.GetByteCount(KeyName)
                + SerializedValuesSize();
            return MaxDataSize - used;
        }
    }

    public static KeyPage Read(byte[] buffer, int pageNumber)
    {
        var page = new KeyPage { PageNumber = pageNumber };
        page.IsDeleted = (buffer[1] & 0x01) != 0;
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4));
        page.ParentPage = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(6));
        page.FirstChildPage = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(10));
        page.NextSiblingPage = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(14));
        int valueCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(18));

        int pos = FixedHeaderSize;
        page.KeyName = Encoding.UTF8.GetString(buffer, pos, nameLen);
        pos += nameLen;

        page.Values = new List<ValueEntry>(valueCount);
        for (int i = 0; i < valueCount; i++)
        {
            var entry = ReadValueEntry(buffer, ref pos);
            page.Values.Add(entry);
        }

        return page;
    }

    public void Write(byte[] buffer)
    {
        if (buffer.Length < FileHeader.PageSize)
            throw new ArgumentException($"Buffer too small: {buffer.Length} < {FileHeader.PageSize}");

        Array.Clear(buffer, 0, FileHeader.PageSize);
        buffer[0] = (byte)PageType.Key;
        if (IsDeleted) buffer[1] |= 0x01;

        byte[] nameBytes = Encoding.UTF8.GetBytes(KeyName);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(6), ParentPage);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(10), FirstChildPage);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(14), NextSiblingPage);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(18), (ushort)Values.Count);

        int pos = FixedHeaderSize;
        Array.Copy(nameBytes, 0, buffer, pos, nameBytes.Length);
        pos += nameBytes.Length;

        int maxPos = MaxDataSize;
        foreach (var entry in Values)
        {
            int entrySize = ValueEntryHeaderSize + Encoding.UTF8.GetByteCount(entry.Name)
                + (entry.OverflowPage == 0 ? entry.InlineData.Length : 0);
            if (pos + entrySize > maxPos)
                throw new InvalidOperationException(
                    $"Key page overflow: {Values.Count} values exceed page size {maxPos} bytes");
            WriteValueEntry(buffer, ref pos, entry);
        }

        // CRC16 is written externally by PageManager.WritePage
    }

    public bool CanFitInline(int nameBytes, int dataLength) =>
        ValueEntryHeaderSize + nameBytes + dataLength <= AvailableSpace;

    public bool CanFitEntry(int nameBytes) =>
        ValueEntryHeaderSize + nameBytes <= AvailableSpace;

    public void AddInlineValue(string name, RegistryValueKind kind, byte[] data)
    {
        Values.Add(new ValueEntry
        {
            Name = name,
            Kind = kind,
            DataLength = data.Length,
            OverflowPage = 0,
            InlineData = data,
        });
    }

    public void AddOverflowValue(string name, RegistryValueKind kind, int dataLength, int overflowPage)
    {
        Values.Add(new ValueEntry
        {
            Name = name,
            Kind = kind,
            DataLength = dataLength,
            OverflowPage = overflowPage,
            InlineData = Array.Empty<byte>(),
        });
    }

    public bool RemoveValue(string name)
    {
        int index = Values.FindIndex(v => v.Name == name);
        if (index < 0) return false;
        Values.RemoveAt(index);
        return true;
    }

    public ValueEntry? FindValue(string name) => Values.Find(v => v.Name == name);

    private static ValueEntry ReadValueEntry(byte[] buffer, ref int pos)
    {
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos));
        var kind = (RegistryValueKind)BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 2));
        int dataLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(pos + 4));
        int overflowPage = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(pos + 8));

        pos += ValueEntryHeaderSize;
        string name = Encoding.UTF8.GetString(buffer, pos, nameLen);
        pos += nameLen;

        byte[] inlineData;
        if (overflowPage == 0)
        {
            inlineData = new byte[dataLen];
            Array.Copy(buffer, pos, inlineData, 0, dataLen);
            pos += dataLen;
        }
        else
        {
            inlineData = Array.Empty<byte>();
        }

        return new ValueEntry
        {
            Name = name,
            Kind = kind,
            DataLength = dataLen,
            OverflowPage = overflowPage,
            InlineData = inlineData,
        };
    }

    private static void WriteValueEntry(byte[] buffer, ref int pos, ValueEntry entry)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(entry.Name);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), (ushort)entry.Kind);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(pos + 4), entry.DataLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(pos + 8), entry.OverflowPage);
        pos += ValueEntryHeaderSize;

        Array.Copy(nameBytes, 0, buffer, pos, nameBytes.Length);
        pos += nameBytes.Length;

        if (entry.OverflowPage == 0 && entry.InlineData.Length > 0)
        {
            Array.Copy(entry.InlineData, 0, buffer, pos, entry.InlineData.Length);
            pos += entry.InlineData.Length;
        }
    }

    private int SerializedValuesSize()
    {
        int size = 0;
        foreach (var v in Values)
        {
            size += ValueEntryHeaderSize + Encoding.UTF8.GetByteCount(v.Name);
            if (v.OverflowPage == 0)
                size += v.InlineData.Length;
        }
        return size;
    }
}
