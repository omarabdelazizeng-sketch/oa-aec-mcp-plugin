using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using OaAecMcpPlugin.Services;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Read-only. Reports columns, floors, walls, doors, windows and their types in millimetres, so a checker can verify
///     the model against a spec independently of the apply_* builders. Raw facts only — no comparison.
///
///     Params (optional): { "categories": ["structural_columns", "columns", "floors", "walls", "doors", "windows", "types"] }
///     Default: all of them.
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on Revit's main thread.
/// </summary>
public class GetModelElementsCommand : ICommand
{
    public string Name => "get_model_elements";

    private static readonly string[] All = { "structural_columns", "columns", "floors", "walls", "doors", "windows", "types" };

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null) throw new InvalidOperationException("No active Revit document");
        if (doc.IsFamilyDocument) throw new ArgumentException("get_model_elements needs a project document");

        var wanted = new HashSet<string>(All, StringComparer.Ordinal);
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("categories", out var cats)
            && cats.ValueKind == JsonValueKind.Array)
        {
            wanted = cats.EnumerateArray().Select(c => c.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
            var unknown = wanted.Except(All).ToList();
            if (unknown.Count > 0) throw new ArgumentException($"unknown categories: {string.Join(", ", unknown)}");
        }

        var levelNames = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .ToDictionary(l => l.Id, l => l);

        var result = new Dictionary<string, object> { ["document"] = doc.Title, ["units"] = "mm" };
        if (wanted.Contains("structural_columns")) result["structural_columns"] = Columns(doc, BuiltInCategory.OST_StructuralColumns, levelNames);
        if (wanted.Contains("columns")) result["columns"] = Columns(doc, BuiltInCategory.OST_Columns, levelNames);
        if (wanted.Contains("floors")) result["floors"] = Floors(doc, levelNames);
        if (wanted.Contains("walls")) result["walls"] = Walls(doc, levelNames);
        if (wanted.Contains("doors")) result["doors"] = Openings(doc, BuiltInCategory.OST_Doors, levelNames);
        if (wanted.Contains("windows")) result["windows"] = Openings(doc, BuiltInCategory.OST_Windows, levelNames);
        if (wanted.Contains("types")) result["types"] = Types(doc);
        return result;
    }

    private static string? LevelName(Dictionary<ElementId, Level> levels, ElementId id) =>
        levels.TryGetValue(id, out var l) ? l.Name : null;

    private static double Len(Element e, BuiltInParameter bip) =>
        MmUnits.FromFt(e.get_Parameter(bip)?.AsDouble() ?? 0);

    private static List<object> Columns(Document doc, BuiltInCategory cat, Dictionary<ElementId, Level> levels) =>
        new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType()
            .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Select(c =>
            {
                var lp = c.Location as LocationPoint;
                var baseId = c.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                var topId = c.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                var baseOff = Len(c, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                var topOff = Len(c, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                double? bottomZ = levels.TryGetValue(baseId, out var bl) ? MmUnits.FromFt(bl.ProjectElevation) + baseOff : null;
                double? topZ = levels.TryGetValue(topId, out var tl) ? MmUnits.FromFt(tl.ProjectElevation) + topOff : null;
                return (object)new
                {
                    id = c.Id.ToLong(), mark = ModelLookup.Mark(c), family = c.Symbol.FamilyName, type = c.Symbol.Name,
                    x_mm = lp != null ? MmUnits.FromFt(lp.Point.X) : (double?)null,
                    y_mm = lp != null ? MmUnits.FromFt(lp.Point.Y) : (double?)null,
                    rotation_deg = lp != null ? lp.Rotation * 180.0 / Math.PI : (double?)null,
                    base_level = LevelName(levels, baseId), base_offset_mm = baseOff,
                    top_level = LevelName(levels, topId), top_offset_mm = topOff,
                    bottom_z_mm = bottomZ, top_z_mm = topZ,
                };
            }).ToList();

    private static List<object> Floors(Document doc, Dictionary<ElementId, Level> levels) =>
        new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>()
            .Select(f =>
            {
                var loops = new List<List<double[]>>();
                var curved = false;
                if (doc.GetElement(f.SketchId) is Sketch sketch)
                    foreach (CurveArray arr in sketch.Profile)
                    {
                        var pts = new List<double[]>();
                        foreach (Curve c in arr)
                        {
                            if (c is not Line) curved = true;
                            var p = c.GetEndPoint(0);
                            pts.Add(new[] { MmUnits.FromFt(p.X), MmUnits.FromFt(p.Y) });
                        }
                        loops.Add(pts);
                    }
                var type = doc.GetElement(f.GetTypeId()) as FloorType;
                return (object)new
                {
                    id = f.Id.ToLong(), mark = ModelLookup.Mark(f), type = type?.Name,
                    level = LevelName(levels, f.LevelId),
                    offset_mm = Len(f, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM),
                    structural = f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1,
                    thickness_mm = type?.GetCompoundStructure() is { } cs ? MmUnits.FromFt(cs.GetWidth()) : (double?)null,
                    has_curved_edges = curved,
                    loops_mm = loops,
                };
            }).ToList();

    private static List<object> Walls(Document doc, Dictionary<ElementId, Level> levels) =>
        new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
            .Select(w =>
            {
                var curve = (w.Location as LocationCurve)?.Curve;
                var topId = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
                return (object)new
                {
                    id = w.Id.ToLong(), mark = ModelLookup.Mark(w), type = w.WallType.Name, kind = w.WallType.Kind.ToString(),
                    width_mm = MmUnits.FromFt(w.WallType.Width),
                    start_mm = curve != null ? MmUnits.Of(curve.GetEndPoint(0)) : null,
                    end_mm = curve != null ? MmUnits.Of(curve.GetEndPoint(1)) : null,
                    is_line = curve is Line,
                    has_curved_edges = curve != null && curve is not Line,
                    location_line = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger(),
                    location_line_name = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM) is { } location
                        ? ApplyWallsCommand.LocationLineName((WallLocationLine)location.AsInteger()) : null,
                    structural_usage = w.StructuralUsage.ToString(),
                    structural = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1,
                    flipped = w.Flipped,
                    base_level = LevelName(levels, w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId),
                    base_offset_mm = Len(w, BuiltInParameter.WALL_BASE_OFFSET),
                    top_level = LevelName(levels, topId),
                    top_offset_mm = Len(w, BuiltInParameter.WALL_TOP_OFFSET),
                    unconnected_height_mm = Len(w, BuiltInParameter.WALL_USER_HEIGHT_PARAM),
                };
            }).ToList();

    private static List<object> Openings(Document doc, BuiltInCategory category, Dictionary<ElementId, Level> levels) =>
        new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType()
            .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Select(instance =>
            {
                var lp = instance.Location as LocationPoint;
                var host = instance.Host as Wall;
                return (object)new
                {
                    id = instance.Id.ToLong(), mark = ModelLookup.Mark(instance),
                    family = instance.Symbol.FamilyName, type = instance.Symbol.Name,
                    level = LevelName(levels, instance.LevelId),
                    host_wall_mark = host != null ? ModelLookup.Mark(host) : null,
                    location_mm = lp != null ? new[] { MmUnits.FromFt(lp.Point.X), MmUnits.FromFt(lp.Point.Y) } : null,
                    sill_height_mm = OpeningFacts.Mm(OpeningFacts.Length(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)),
                    head_height_mm = OpeningFacts.Mm(OpeningFacts.Length(instance, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM)),
                    width_mm = OpeningFacts.Mm(OpeningFacts.Dimension(instance, width: true)
                                              ?? OpeningFacts.Dimension(instance.Symbol, width: true)),
                    height_mm = OpeningFacts.Mm(OpeningFacts.Dimension(instance, width: false)
                                               ?? OpeningFacts.Dimension(instance.Symbol, width: false)),
                    hand_flipped = instance.HandFlipped, facing_flipped = instance.FacingFlipped,
                    rotation_deg = lp != null ? lp.Rotation * 180.0 / Math.PI : (double?)null,
                };
            }).ToList();

    private static object OpeningTypes(Document doc, BuiltInCategory category) =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>()
            .Select(symbol => new
            {
                family = symbol.FamilyName, type = symbol.Name,
                width_mm = OpeningFacts.Mm(OpeningFacts.Dimension(symbol, width: true)),
                height_mm = OpeningFacts.Mm(OpeningFacts.Dimension(symbol, width: false)),
            }).ToList();

    private static object Types(Document doc)
    {
        static object Layers(HostObjAttributes t) => t.GetCompoundStructure()?.GetLayers()
            .Select(l => new { function = l.Function.ToString(), width_mm = MmUnits.FromFt(l.Width), material_id = l.MaterialId.ToLong() })
            .ToList() ?? (object)new List<object>();

        static object Symbols(Document doc, BuiltInCategory cat) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(cat).Cast<FamilySymbol>()
                .Select(s => new
                {
                    id = s.Id.ToLong(), family = s.FamilyName, type = s.Name,
                    length_params_mm = s.Parameters.Cast<Parameter>()
                        .Where(p => p.StorageType == StorageType.Double && p.Definition != null
                                    && p.Definition.GetDataType() == SpecTypeId.Length)
                        .GroupBy(p => p.Definition.Name)
                        .ToDictionary(g => g.Key, g => MmUnits.FromFt(g.First().AsDouble())),
                }).ToList();

        return new
        {
            structural_column_types = Symbols(doc, BuiltInCategory.OST_StructuralColumns),
            column_types = Symbols(doc, BuiltInCategory.OST_Columns),
            door_types = OpeningTypes(doc, BuiltInCategory.OST_Doors),
            window_types = OpeningTypes(doc, BuiltInCategory.OST_Windows),
            floor_types = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                .Select(t => new { id = t.Id.ToLong(), name = t.Name, layers = Layers(t) }).ToList(),
            wall_types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Select(t => new { id = t.Id.ToLong(), name = t.Name, kind = t.Kind.ToString(), layers = Layers(t) }).ToList(),
        };
    }
}
