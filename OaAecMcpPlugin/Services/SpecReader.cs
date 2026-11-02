using Autodesk.Revit.DB;
using System.Text.Json;

namespace OaAecMcpPlugin.Services;

/// <summary>
///     Strict readers for JSON-RPC params shared by the modeling commands.
///     Every problem throws <see cref="ArgumentException"/>, which the WebSocket server maps to -32602.
///     Pure parsing — no Revit document access.
/// </summary>
public static class SpecReader
{
    public static JsonElement Obj(JsonElement p, string what = "params")
    {
        if (p.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"{what} must be a JSON object");
        return p;
    }

    public static List<JsonElement> Items(JsonElement e, string key, bool required = false)
    {
        if (e.TryGetProperty(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().ToList();
            if (v.ValueKind != JsonValueKind.Null) throw new ArgumentException($"'{key}' must be an array");
        }
        if (required) throw new ArgumentException($"'{key}' is required");
        return new List<JsonElement>();
    }

    public static string Str(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
            throw new ArgumentException($"'{key}' must be a non-empty string");
        return v.GetString()!.Trim();
    }

    public static string? OptStr(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw new ArgumentException($"'{key}' must be a string");
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public static double Num(JsonElement e, string key, double? fallback = null)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            if (fallback.HasValue) return fallback.Value;
            throw new ArgumentException($"'{key}' is required");
        }
        if (v.ValueKind != JsonValueKind.Number || !double.IsFinite(v.GetDouble()))
            throw new ArgumentException($"'{key}' must be a finite number");
        return v.GetDouble();
    }

    public static bool Bool(JsonElement e, string key, bool fallback)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"'{key}' must be true or false"),
        };
    }

    /// <summary>A polygon given as [[x,y], ...] in mm. A repeated closing point is dropped.</summary>
    public static List<(double X, double Y)> Polygon(JsonElement v, string what)
    {
        if (v.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{what} must be an array of [x, y] points in mm");
        var pts = new List<(double X, double Y)>();
        foreach (var p in v.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2
                || p[0].ValueKind != JsonValueKind.Number || p[1].ValueKind != JsonValueKind.Number)
                throw new ArgumentException($"{what}: every point must be [x, y] numbers in mm");
            pts.Add((p[0].GetDouble(), p[1].GetDouble()));
        }
        if (pts.Count > 1 && Dist(pts[0], pts[^1]) < 1e-6) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) throw new ArgumentException($"{what} needs at least 3 distinct points");
        for (var i = 0; i < pts.Count; i++)
            if (Dist(pts[i], pts[(i + 1) % pts.Count]) < 1.0)
                throw new ArgumentException($"{what}: segment {i} is shorter than 1 mm");
        if (Math.Abs(SignedArea(pts)) < 1.0)
            throw new ArgumentException($"{what} has (almost) zero area");
        return pts;
    }

    public static double SignedArea(IReadOnlyList<(double X, double Y)> pts)
    {
        double a = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var (x0, y0) = pts[i];
            var (x1, y1) = pts[(i + 1) % pts.Count];
            a += x0 * y1 - x1 * y0;
        }
        return a / 2.0;
    }

    public static void Unique(IEnumerable<string> names, string kind)
    {
        var dup = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new ArgumentException($"duplicate {kind} '{dup.Key}'");
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>Millimetre ↔ Revit internal feet conversions.</summary>
public static class MmUnits
{
    public static double ToFt(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    public static double FromFt(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
    public static XYZ Xyz(double x, double y, double z = 0) => new(ToFt(x), ToFt(y), ToFt(z));
    public static double[] Of(XYZ p) => new[] { FromFt(p.X), FromFt(p.Y), FromFt(p.Z) };
}
