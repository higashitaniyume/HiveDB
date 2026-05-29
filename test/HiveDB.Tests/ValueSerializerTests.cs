using HiveDB;
using HiveDB.Value;

namespace HiveDB.Tests;

public class ValueSerializerTests
{
    [Fact]
    public void String_RoundTrip()
    {
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.String, "hello world");
        object result = ValueSerializer.Deserialize(RegistryValueKind.String, data);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void String_NullTerminator_IsTrimmed()
    {
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.String, "test");
        Assert.Equal(5, data.Length); // 4 chars + null
        Assert.Equal(0, data[^1]); // null terminator
        object result = ValueSerializer.Deserialize(RegistryValueKind.String, data);
        Assert.Equal("test", result);
    }

    [Fact]
    public void DWord_RoundTrip()
    {
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.DWord, 42);
        Assert.Equal(4, data.Length);
        object result = ValueSerializer.Deserialize(RegistryValueKind.DWord, data);
        Assert.Equal(42, result);
    }

    [Fact]
    public void DWord_NegativeValue_RoundTrip()
    {
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.DWord, -100);
        object result = ValueSerializer.Deserialize(RegistryValueKind.DWord, data);
        Assert.Equal(-100, result);
    }

    [Fact]
    public void QWord_RoundTrip()
    {
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.QWord, 1234567890123L);
        Assert.Equal(8, data.Length);
        object result = ValueSerializer.Deserialize(RegistryValueKind.QWord, data);
        Assert.Equal(1234567890123L, result);
    }

    [Fact]
    public void Binary_RoundTrip()
    {
        byte[] input = { 0x01, 0x02, 0x03, 0xFF, 0x00 };
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.Binary, input);
        object result = ValueSerializer.Deserialize(RegistryValueKind.Binary, data);
        Assert.Equal(input, (byte[])result);
    }

    [Fact]
    public void MultiString_RoundTrip()
    {
        string[] input = { "hello", "world", "foo" };
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.MultiString, input);
        object result = ValueSerializer.Deserialize(RegistryValueKind.MultiString, data);
        Assert.Equal(input, (string[])result);
    }

    [Fact]
    public void MultiString_EmptyArray()
    {
        string[] input = Array.Empty<string>();
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.MultiString, input);
        object result = ValueSerializer.Deserialize(RegistryValueKind.MultiString, data);
        Assert.Empty((string[])result);
    }

    [Fact]
    public void MultiString_SingleEntry()
    {
        string[] input = { "only" };
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.MultiString, input);
        object result = ValueSerializer.Deserialize(RegistryValueKind.MultiString, data);
        Assert.Single((string[])result);
        Assert.Equal("only", ((string[])result)[0]);
    }

    [Fact]
    public void ExpandString_RoundTrip()
    {
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.ExpandString, "%PATH%");
        object result = ValueSerializer.Deserialize(RegistryValueKind.ExpandString, data);
        Assert.Equal("%PATH%", result);
    }

    [Fact]
    public void None_RoundTrip()
    {
        byte[] input = { 0x0A, 0x0B };
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.None, input);
        object result = ValueSerializer.Deserialize(RegistryValueKind.None, data);
        Assert.Equal(input, (byte[])result);
    }

    [Fact]
    public void String_Empty()
    {
        byte[] data = ValueSerializer.Serialize(RegistryValueKind.String, "");
        object result = ValueSerializer.Deserialize(RegistryValueKind.String, data);
        Assert.Equal("", result);
    }
}
