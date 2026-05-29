using System.Buffers.Binary;

namespace HiveDB.Storage;

/// <summary>
/// Protection mode for a HiveDB database.
/// </summary>
internal enum ProtectionMode : uint
{
    None = 0,
    /// <summary>AES-256-GCM: encrypt + authenticate every data page.</summary>
    Encrypted = 1,
    /// <summary>HMAC-SHA256: authenticate every data page without encryption.</summary>
    Signed = 2,
}

/// <summary>
/// Protection metadata stored in the file header (page 0) at offset 28+.
/// Size: 4 (flags) + 16 (salt) + 32 (keyCheckHash) + 4 (iterations) = 56 bytes.
/// Flags layout: bit 0-1 = mode (0=None, 1=Encrypted, 2=Signed), bit 2+ = reserved.
/// </summary>
internal sealed class EncryptionHeader
{
    public const int Size = 56;
    public const int HeaderOffset = 28;

    public ProtectionMode Mode { get; set; }
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public byte[] KeyCheckHash { get; set; } = Array.Empty<byte>();
    public int Pbkdf2Iterations { get; set; }

    public bool IsProtected => Mode != ProtectionMode.None;

    /// <summary>
    /// Reads protection metadata from a header page buffer at the given offset.
    /// Returns null if the database has no protection (mode == None).
    /// </summary>
    public static EncryptionHeader? Read(byte[] buffer, int offset)
    {
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
        var mode = (ProtectionMode)(flags & 3);
        if (mode == ProtectionMode.None)
            return null;

        int iterations = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 52));

        var header = new EncryptionHeader
        {
            Mode = mode,
            Salt = buffer.AsSpan(offset + 4, 16).ToArray(),
            KeyCheckHash = buffer.AsSpan(offset + 20, 32).ToArray(),
            Pbkdf2Iterations = iterations > 0 ? iterations : CryptoManager.DefaultIterations,
        };
        return header;
    }

    /// <summary>
    /// Writes protection metadata to a header page buffer at the given offset.
    /// </summary>
    public void Write(byte[] buffer, int offset)
    {
        uint flags = (uint)Mode;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), flags);

        Salt.AsSpan(0, Math.Min(Salt.Length, 16)).CopyTo(buffer.AsSpan(offset + 4, 16));
        KeyCheckHash.AsSpan(0, Math.Min(KeyCheckHash.Length, 32)).CopyTo(buffer.AsSpan(offset + 20, 32));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset + 52), Pbkdf2Iterations);
    }
}
