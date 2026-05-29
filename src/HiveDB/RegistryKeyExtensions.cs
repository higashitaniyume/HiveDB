namespace HiveDB;

public static class RegistryKeyExtensions
{
    public static T? GetValue<T>(this RegistryKey key, string name, T? defaultValue = default)
    {
        object? value = key.GetValue(name, defaultValue);
        if (value is null) return defaultValue;
        if (value is T typed) return typed;

        // Handle numeric conversions
        if (typeof(T) == typeof(int) && value is long lv)
            return (T)(object)(int)lv;
        if (typeof(T) == typeof(long) && value is int iv)
            return (T)(object)(long)iv;

        throw new InvalidCastException(
            $"Cannot cast value of type {value.GetType()} to {typeof(T)}.");
    }
}
