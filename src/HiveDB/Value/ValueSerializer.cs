using System.Text;

namespace HiveDB.Value;

internal static class ValueSerializer
{
    public static byte[] Serialize(RegistryValueKind kind, object value)
    {
        return kind switch
        {
            RegistryValueKind.String => SerializeString((string)value),
            RegistryValueKind.ExpandString => SerializeString((string)value),
            RegistryValueKind.DWord => SerializeDWord((int)value),
            RegistryValueKind.QWord => SerializeQWord((long)value),
            RegistryValueKind.Binary => (byte[])value,
            RegistryValueKind.MultiString => SerializeMultiString((string[])value),
            RegistryValueKind.None => value is byte[] b ? b : throw new ArgumentException("REG_NONE requires byte[] value."),
            _ => throw new ArgumentException($"Unknown value kind: {kind}"),
        };
    }

    public static object Deserialize(RegistryValueKind kind, byte[] data)
    {
        return kind switch
        {
            RegistryValueKind.String => DeserializeString(data),
            RegistryValueKind.ExpandString => DeserializeString(data),
            RegistryValueKind.DWord => DeserializeDWord(data),
            RegistryValueKind.QWord => DeserializeQWord(data),
            RegistryValueKind.Binary => data,
            RegistryValueKind.MultiString => DeserializeMultiString(data),
            RegistryValueKind.None => data,
            _ => throw new ArgumentException($"Unknown value kind: {kind}"),
        };
    }

    public static object InferKindAndDeserialize(string name, byte[] data)
    {
        // Values without explicit kind default to String for backward compatibility.
        // This is a minimal heuristic: if they come from SetValue without kind,
        // the kind would have been inferred on the way in.
        // For recovery or unknown kind, we return the raw bytes.
        return data;
    }

    private static byte[] SerializeString(string value)
    {
        byte[] str = Encoding.UTF8.GetBytes(value);
        var result = new byte[str.Length + 1];
        Array.Copy(str, result, str.Length);
        return result; // null terminator from zero-initialization
    }

    private static string DeserializeString(byte[] data)
    {
        int len = data.Length;
        while (len > 0 && data[len - 1] == 0) len--;
        return Encoding.UTF8.GetString(data, 0, len);
    }

    private static byte[] SerializeDWord(int value)
    {
        var result = new byte[4];
        result[0] = (byte)value;
        result[1] = (byte)(value >> 8);
        result[2] = (byte)(value >> 16);
        result[3] = (byte)(value >> 24);
        return result;
    }

    private static int DeserializeDWord(byte[] data)
    {
        if (data.Length < 4)
            throw new ArgumentException("DWord data must be at least 4 bytes.");
        return data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
    }

    private static byte[] SerializeQWord(long value)
    {
        var result = new byte[8];
        result[0] = (byte)value;
        result[1] = (byte)(value >> 8);
        result[2] = (byte)(value >> 16);
        result[3] = (byte)(value >> 24);
        result[4] = (byte)(value >> 32);
        result[5] = (byte)(value >> 40);
        result[6] = (byte)(value >> 48);
        result[7] = (byte)(value >> 56);
        return result;
    }

    private static long DeserializeQWord(byte[] data)
    {
        if (data.Length < 8)
            throw new ArgumentException("QWord data must be at least 8 bytes.");
        return (long)data[0]
            | ((long)data[1] << 8)
            | ((long)data[2] << 16)
            | ((long)data[3] << 24)
            | ((long)data[4] << 32)
            | ((long)data[5] << 40)
            | ((long)data[6] << 48)
            | ((long)data[7] << 56);
    }

    private static byte[] SerializeMultiString(string[] values)
    {
        int total = 0;
        foreach (var v in values)
            total += Encoding.UTF8.GetByteCount(v) + 1; // null terminator per string
        total += 1; // final null terminator

        var result = new byte[total];
        int pos = 0;
        foreach (var v in values)
        {
            int written = Encoding.UTF8.GetBytes(v, 0, v.Length, result, pos);
            pos += written;
            result[pos++] = 0;
        }
        result[pos] = 0;
        return result;
    }

    private static string[] DeserializeMultiString(byte[] data)
    {
        if (data.Length == 0)
            return Array.Empty<string>();

        var strings = new List<string>();
        int start = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == 0)
            {
                if (i > start)
                    strings.Add(Encoding.UTF8.GetString(data, start, i - start));
                start = i + 1;
                // Double null = end of list
                if (i + 1 < data.Length && data[i + 1] == 0)
                    break;
            }
        }
        return strings.ToArray();
    }
}
