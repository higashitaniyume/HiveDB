namespace HiveDB;

public sealed class HiveDBException : IOException
{
    public HiveDBException(string message) : base(message) { }
    public HiveDBException(string message, Exception inner) : base(message, inner) { }
}
