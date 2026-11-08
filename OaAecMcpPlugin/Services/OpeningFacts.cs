using Autodesk.Revit.DB;

namespace OaAecMcpPlugin.Services;

/// <summary>Nullable length facts shared by door/window placement validation and independent read-back.</summary>
internal static class OpeningFacts
{
    /// <summary>Reads a populated length parameter in internal feet; absent/unreadable values stay null.</summary>
    internal static double? Length(Element element, BuiltInParameter parameter)
    {
        var p = element.get_Parameter(parameter);
        return p is { HasValue: true, StorageType: StorageType.Double }
               && p.Definition.GetDataType() == SpecTypeId.Length && SpecReader.IsFinite(p.AsDouble())
            ? p.AsDouble() : null;
    }

    /// <summary>Reads standard width/height parameters, then an unambiguous named length parameter.</summary>
    internal static double? Dimension(Element element, bool width)
    {
        var value = Length(element, width ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.DOOR_HEIGHT)
                    ?? Length(element, width ? BuiltInParameter.WINDOW_WIDTH : BuiltInParameter.WINDOW_HEIGHT)
                    ?? Length(element, width ? BuiltInParameter.FAMILY_WIDTH_PARAM : BuiltInParameter.FAMILY_HEIGHT_PARAM);
        if (value.HasValue) return value;
        var named = element.GetParameters(width ? "Width" : "Height")
            .Where(p => p.HasValue && p.StorageType == StorageType.Double
                        && p.Definition.GetDataType() == SpecTypeId.Length).ToList();
        return named.Count == 1 && SpecReader.IsFinite(named[0].AsDouble()) ? named[0].AsDouble() : null;
    }

    /// <summary>Converts a readable internal length to millimetres without turning missing data into zero.</summary>
    internal static double? Mm(double? feet) => feet.HasValue ? MmUnits.FromFt(feet.Value) : null;
}
