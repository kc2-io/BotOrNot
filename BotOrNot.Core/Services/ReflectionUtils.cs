using System.Collections.Concurrent;
using System.Reflection;

namespace BotOrNot.Core.Services;

public static class ReflectionUtils
{
    private static readonly ConcurrentDictionary<(Type, string), Lazy<PropertyInfo?>> Cache = new();

    private static PropertyInfo? FindProp(Type t, string name)
    {
        return Cache.GetOrAdd((t, name), static key => new Lazy<PropertyInfo?>(() =>
            key.Item1.GetProperty(
                key.Item2,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public static object? GetObject(object? obj, string prop)
    {
        if (obj is null) return null;
        var pi = FindProp(obj.GetType(), prop);
        return pi?.GetValue(obj);
    }

    public static string? FirstString(object? obj, params string[] names)
    {
        if (obj is null) return null;
        var t = obj.GetType();
        foreach (var n in names)
        {
            var pi = FindProp(t, n);
            if (pi == null) continue;
            var v = pi.GetValue(obj);
            if (v == null) continue;
            var s = v.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }

    public static float? GetFloat(object? obj, string name)
    {
        if (obj is null) return null;
        var pi = FindProp(obj.GetType(), name);
        if (pi == null) return null;
        var v = pi.GetValue(obj);
        if (v is float f) return f;
        if (v is double d) return (float)d;
        if (float.TryParse(v?.ToString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)) return parsed;
        return null;
    }

    public static double? GetDouble(object? obj, string name)
    {
        if (obj is null) return null;
        var pi = FindProp(obj.GetType(), name);
        if (pi == null) return null;
        var value = pi.GetValue(obj);
        if (value is double doubleValue) return doubleValue;
        if (value is float floatValue) return floatValue;
        if (double.TryParse(value?.ToString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)) return parsed;
        return null;
    }

    public static int? GetInt(object? obj, string name)
    {
        if (obj is null) return null;
        var pi = FindProp(obj.GetType(), name);
        if (pi == null) return null;
        var value = pi.GetValue(obj);
        if (value is int intValue) return intValue;
        if (value is uint uintValue && uintValue <= int.MaxValue) return (int)uintValue;
        if (int.TryParse(value?.ToString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)) return parsed;
        return null;
    }

    public static uint? GetUInt(object? obj, string name)
    {
        if (obj is null) return null;
        var pi = FindProp(obj.GetType(), name);
        if (pi == null) return null;
        var value = pi.GetValue(obj);
        if (value is uint uintValue) return uintValue;
        if (value is int intValue && intValue >= 0) return (uint)intValue;
        if (uint.TryParse(value?.ToString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)) return parsed;
        return null;
    }

    public static bool GetBool(object? obj, string name)
    {
        if (obj is null) return false;
        var pi = FindProp(obj.GetType(), name);
        if (pi == null) return false;
        var v = pi.GetValue(obj);
        if (v is bool b) return b;
        if (v is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
