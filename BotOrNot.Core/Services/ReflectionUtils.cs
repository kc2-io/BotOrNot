using System.Reflection;

namespace BotOrNot.Core.Services;

public static class ReflectionUtils
{
    private static readonly Dictionary<(Type, string), PropertyInfo?> Cache = new();

    private static PropertyInfo? FindProp(Type t, string name)
    {
        var key = (t, name);
        if (Cache.TryGetValue(key, out var pi)) return pi;
        pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        Cache[key] = pi;
        return pi;
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