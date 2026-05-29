namespace HiveDB.Storage;

internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320;
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = (crc >> 8) ^ Table[(byte)(crc ^ b)];
        return crc ^ 0xFFFFFFFF;
    }
}
