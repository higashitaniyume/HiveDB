using System.Security.Cryptography;
using System.Text;

namespace HiveDB.Storage;

/// <summary>
/// Cryptographic engine for HiveDB page protection.
/// Supports two modes via <see cref="ProtectionMode"/>:
///   Encrypted — AES-256-GCM (encryption + authentication) — net8.0+ only
///   Signed    — HMAC-SHA256 (authentication only, no encryption) — all targets
///
/// Both modes reserve a 28-byte footer at the end of each 4096-byte page:
///   [0..4067]  Payload data           (4068 bytes)
///   [4068..4079] Nonce (GCM) / zeros (HMAC) (12 bytes)
///   [4080..4095] Tag (GCM) / HMAC (16 bytes)
/// </summary>
internal sealed class CryptoManager
{
    public const int DefaultIterations = 600_000;
    public const int SaltSize = 16;
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int MacSize = 16;       // GCM tag or truncated HMAC
    public const int FooterSize = NonceSize + MacSize; // 28 bytes
    /// <summary>Maximum data bytes writable in a page (bytes 0..4067). Used by KeyPage.</summary>
    public const int MaxDataSize = PageSize - FooterSize; // 4068

    private readonly byte[] _key;
    private readonly ProtectionMode _mode;

    public CryptoManager(byte[] key, ProtectionMode mode)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes, got {key.Length}.");
        _key = (byte[])key.Clone();
        _mode = mode;
    }

    public ProtectionMode Mode => _mode;

    // ── Static key derivation ──────────────────────────────

    /// <summary>
    /// Derives a 32-byte key from a password using PBKDF2-HMAC-SHA256.
    /// </summary>
    public static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        using var pbkdf2 = new Rfc2898DeriveBytes(passwordBytes, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }

    /// <summary>
    /// Generates a random 16-byte salt.
    /// </summary>
    public static byte[] GenerateSalt()
    {
        byte[] salt = new byte[SaltSize];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(salt);
        return salt;
    }

    /// <summary>
    /// Computes a key check hash for password verification without decrypting data.
    /// </summary>
    public static byte[] ComputeKeyCheckHash(byte[] key)
    {
        byte[] context = Encoding.UTF8.GetBytes("HiveDB-KeyCheck-V1");
        byte[] combined = new byte[key.Length + context.Length];
        Buffer.BlockCopy(key, 0, combined, 0, key.Length);
        Buffer.BlockCopy(context, 0, combined, key.Length, context.Length);
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(combined);
    }

    // ── Page protection ────────────────────────────────────

    /// <summary>
    /// Protects a page buffer in-place, writing footer data at the end.
    /// For GCM: encrypts payload + writes nonce + tag.
    /// For HMAC: computes HMAC over payload + writes at footer.
    /// </summary>
    public void ProtectPage(byte[] buffer)
    {
        if (buffer.Length != PageSize)
            throw new ArgumentException($"Buffer must be {PageSize} bytes.");

        switch (_mode)
        {
            case ProtectionMode.Encrypted:
#if NET8_0_OR_GREATER
                EncryptPageGcm(buffer);
                break;
#else
                throw new PlatformNotSupportedException("AES-GCM encryption requires .NET 8.0 or later.");
#endif
            case ProtectionMode.Signed:
                SignPageHmac(buffer);
                break;
            default:
                throw new InvalidOperationException($"Unknown protection mode: {_mode}");
        }
    }

    /// <summary>
    /// Unprotects a page buffer in-place, verifying footer data.
    /// For GCM: decrypts payload, verifying the tag.
    /// For HMAC: verifies the HMAC matches.
    /// Throws HiveDBException on authentication failure.
    /// </summary>
    public void UnprotectPage(byte[] buffer)
    {
        if (buffer.Length != PageSize)
            throw new ArgumentException($"Buffer must be {PageSize} bytes.");

        switch (_mode)
        {
            case ProtectionMode.Encrypted:
#if NET8_0_OR_GREATER
                DecryptPageGcm(buffer);
                break;
#else
                throw new PlatformNotSupportedException("AES-GCM encryption requires .NET 8.0 or later.");
#endif
            case ProtectionMode.Signed:
                VerifyPageHmac(buffer);
                break;
            default:
                throw new InvalidOperationException($"Unknown protection mode: {_mode}");
        }
    }

    /// <summary>
    /// Returns true if the runtime supports GCM encryption.
    /// </summary>
    public static bool IsGcmAvailable =>
#if NET8_0_OR_GREATER
        true;
#else
        false;
#endif

    // ── GCM implementation (net8.0+ only) ──────────────────

#if NET8_0_OR_GREATER
    private const int GcmPayloadSize = MaxDataSize - 1;   // 4067 (excludes page type byte)
    private const int GcmPayloadOffset = 1;                // byte 0 = page type

    private void EncryptPageGcm(byte[] buffer)
    {
        byte[] nonce = new byte[NonceSize];
        byte[] plaintext = new byte[GcmPayloadSize];
        byte[] associatedData = new byte[] { buffer[0] };

        RandomNumberGenerator.Fill(nonce);

        Buffer.BlockCopy(buffer, GcmPayloadOffset, plaintext, 0, GcmPayloadSize);

        byte[] tag = new byte[MacSize];
        byte[] ciphertext = new byte[GcmPayloadSize];

        using var aes = new AesGcm((ReadOnlySpan<byte>)_key, MacSize);
        aes.Encrypt((ReadOnlySpan<byte>)nonce, (ReadOnlySpan<byte>)plaintext,
                    (Span<byte>)ciphertext, (Span<byte>)tag,
                    (ReadOnlySpan<byte>)associatedData);

        Buffer.BlockCopy(ciphertext, 0, buffer, GcmPayloadOffset, GcmPayloadSize);
        Buffer.BlockCopy(nonce, 0, buffer, NonceOffset, NonceSize);
        Buffer.BlockCopy(tag, 0, buffer, MacOffset, MacSize);
    }

    private void DecryptPageGcm(byte[] buffer)
    {
        byte[] nonce = new byte[NonceSize];
        byte[] tag = new byte[MacSize];
        byte[] ciphertext = new byte[GcmPayloadSize];
        byte[] associatedData = new byte[] { buffer[0] };

        Buffer.BlockCopy(buffer, NonceOffset, nonce, 0, NonceSize);
        Buffer.BlockCopy(buffer, MacOffset, tag, 0, MacSize);
        Buffer.BlockCopy(buffer, GcmPayloadOffset, ciphertext, 0, GcmPayloadSize);

        byte[] plaintext = new byte[GcmPayloadSize];

        try
        {
            using var aes = new AesGcm((ReadOnlySpan<byte>)_key, MacSize);
            aes.Decrypt((ReadOnlySpan<byte>)nonce, (ReadOnlySpan<byte>)ciphertext,
                        (ReadOnlySpan<byte>)tag, (Span<byte>)plaintext,
                        (ReadOnlySpan<byte>)associatedData);
        }
        catch (CryptographicException)
        {
            throw new HiveDBException("Page authentication failed: data may be corrupted or tampered.");
        }

        Buffer.BlockCopy(plaintext, 0, buffer, GcmPayloadOffset, GcmPayloadSize);
    }
#endif

    // ── HMAC implementation (all targets) ──────────────────

    private void SignPageHmac(byte[] buffer)
    {
        // Zero out footer before computing HMAC
        Array.Clear(buffer, NonceOffset, FooterSize);

        // Compute HMAC-SHA256 over entire page, truncate to MacSize bytes
        using var hmac = new HMACSHA256(_key);
        byte[] fullHash = hmac.ComputeHash(buffer, 0, PageSize);

        // Store truncated HMAC at the end
        Buffer.BlockCopy(fullHash, 0, buffer, MacOffset, MacSize);
    }

    private void VerifyPageHmac(byte[] buffer)
    {
        // Read stored HMAC
        byte[] storedMac = new byte[MacSize];
        Buffer.BlockCopy(buffer, MacOffset, storedMac, 0, MacSize);

        // Zero out footer for verification
        Array.Clear(buffer, NonceOffset, FooterSize);

        // Recompute
        using var hmac = new HMACSHA256(_key);
        byte[] fullHash = hmac.ComputeHash(buffer, 0, PageSize);
        byte[] computedMac = new byte[MacSize];
        Buffer.BlockCopy(fullHash, 0, computedMac, 0, MacSize);

        // Restore stored MAC in buffer (so it's idempotent)
        Buffer.BlockCopy(storedMac, 0, buffer, MacOffset, MacSize);

        if (!CryptographicOperations.FixedTimeEquals(storedMac, computedMac))
            throw new HiveDBException("Page authentication failed: data may be corrupted or tampered.");
    }

    // ── Offsets (exposed for validation) ───────────────────

    public const int PageSize = 4096;
    public const int NonceOffset = PageSize - FooterSize;       // 4068
    public const int MacOffset = PageSize - MacSize;             // 4080
}
