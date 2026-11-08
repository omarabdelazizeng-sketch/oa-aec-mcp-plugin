using System.Globalization;

namespace OaAecMcpPlugin.Services;

/// <summary>Rejects almost-axis-aligned plan geometry before it can generate Revit warnings.</summary>
public static class AxisGuard
{
    /// <summary>Checks a straight segment in millimetres against the nearest X/Y axis.</summary>
    public static void Segment(string mark, (double X, double Y) start, (double X, double Y) end,
        string field = "segment")
    {
        var dx = Math.Abs(end.X - start.X);
        var dy = Math.Abs(end.Y - start.Y);
        var angle = Math.Atan2(Math.Min(dx, dy), Math.Max(dx, dy)) * 180.0 / Math.PI;
        if (IsNearAxis(angle))
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                "mark '{0}': {1} [{2:R},{3:R}] -> [{4:R},{5:R}] mm is {6:R} deg from the nearest X/Y axis: slightly off axis; snap it in the spec",
                mark, field, start.X, start.Y, end.X, end.Y, angle));
    }

    /// <summary>Checks a rotation in degrees, including negative rotations and whole turns.</summary>
    public static void Rotation(string mark, double rotationDeg)
    {
        var angle = Math.Abs(Math.IEEERemainder(rotationDeg, 90.0));
        if (IsNearAxis(angle))
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                "column '{0}': rotation_deg {1:R} is {2:R} deg from a multiple of 90 deg: slightly off axis; snap it in the spec",
                mark, rotationDeg, angle));
    }

    private static bool IsNearAxis(double angle) => angle > 1e-6 && angle < 0.5;
}
