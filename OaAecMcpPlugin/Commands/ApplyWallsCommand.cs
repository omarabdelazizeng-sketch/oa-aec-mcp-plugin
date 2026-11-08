using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nice3point.Revit.Extensions;
using OaAecMcpPlugin.Services;
using Serilog;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Creates or replaces straight walls in ONE transaction, matched by Mark within walls.
///     Identical specs leave the existing wall alone; changed specs replace it with the same Mark.
///     Other walls and floors are never explicitly modified; cascading deletion of either is rejected.
///     No explicit joins or cuts are performed (Revit's creation-time joins are allowed).
///
///     Params: { "walls": [{ "mark": "W-01", "type": "Generic 200mm", "level": "Level 1",
///       "base_offset_mm": 0, "top_level": "Level 2", "top_offset_mm": 0,
///       "start": [0,0], "end": [4000,0], "location_line": "Center",
///       "structural_usage": "NonBearing", "flip": false }] }
///     Omit top_level and provide height_mm for an unconnected wall. All lengths are millimetres.
///     start/end describe the API location curve in plan (wall centreline), independently of location_line.
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on Revit's main thread.
/// </summary>
public class ApplyWallsCommand : ICommand
{
    /// <summary>The JSON-RPC command name.</summary>
    public string Name => "apply_walls";

    private const double Tol = 1e-9; // internal feet, as in the other apply commands

    private sealed record WallSpec(string Mark, string Type, string Level, double BaseOffsetMm,
        string? TopLevel, double TopOffsetMm, double HeightMm, (double X, double Y) Start,
        (double X, double Y) End, WallLocationLine LocationLine, StructuralWallUsage Usage, bool Flip);

    /// <summary>Validates and applies the batch on Revit's main thread.</summary>
    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null) throw new InvalidOperationException("No active Revit document");
        if (doc.IsFamilyDocument) throw new ArgumentException("apply_walls needs a project document");

        var specs = Parse(parameters);
        var lookup = new ModelLookup(doc);
        // Resolve names and vertical extents before starting any model changes.
        var resolved = specs.Select(s =>
        {
            var type = Context(s.Mark, "type", () => lookup.GetHostType<WallType>(s.Type));
            var level = Context(s.Mark, "level", () => lookup.GetLevel(s.Level));
            var top = s.TopLevel == null ? null : Context(s.Mark, "top_level", () => lookup.GetLevel(s.TopLevel));
            var height = top == null ? MmUnits.ToFt(s.HeightMm)
                : top.ProjectElevation + MmUnits.ToFt(s.TopOffsetMm) - level.ProjectElevation - MmUnits.ToFt(s.BaseOffsetMm);
            if (!double.IsFinite(height) || height <= 0 || height > 30000)
                throw new ArgumentException($"wall '{s.Mark}': '{(top == null ? "height_mm" : "top_level/top_offset_mm/base_offset_mm")}' must give a positive height no greater than {MmUnits.FromFt(30000)} mm");
            var length = MmUnits.Xyz(s.Start.X, s.Start.Y).DistanceTo(MmUnits.Xyz(s.End.X, s.End.Y));
            if (length < doc.Application.ShortCurveTolerance)
                throw new ArgumentException($"wall '{s.Mark}': 'start/end' length is below Revit's short curve tolerance");
            return (Spec: s, Type: type, Level: level, Top: top, Height: height);
        }).ToList();
        var results = new List<object>();
        var untouched = 0;

        var warnings = RevitTx.Run(doc, "OA-AEC-MCP: apply_walls", () =>
        {
            var existing = new Dictionary<string, Wall>(StringComparer.Ordinal);
            var protectedIds = new FilteredElementCollector(doc).OfClass(typeof(Floor)).ToElementIds().ToHashSet();
            foreach (var wall in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                protectedIds.Add(wall.Id);
                var mark = ModelLookup.Mark(wall);
                if (mark.Length == 0) { untouched++; continue; }
                if (!existing.TryAdd(mark, wall))
                    throw new ArgumentException($"wall '{mark}': duplicate 'mark' in the model; fix duplicates first");
            }
            var wanted = specs.Select(s => s.Mark).ToHashSet(StringComparer.Ordinal);
            untouched += existing.Keys.Count(k => !wanted.Contains(k));
            var applied = new Dictionary<string, Wall>(StringComparer.Ordinal);
            var changed = false;

            foreach (var r in resolved)
            {
                var s = r.Spec;
                string action;
                if (existing.TryGetValue(s.Mark, out var wall))
                {
                    if (Matches(wall, r.Type, r.Level, r.Top, s))
                    {
                        applied.Add(s.Mark, wall);
                        results.Add(new { mark = s.Mark, id = wall.Id.ToLong(), action = "unchanged" });
                        continue;
                    }
                    var oldId = wall.Id;
                    var deleted = doc.Delete(oldId);
                    if (deleted.Any(id => id != oldId && protectedIds.Contains(id)))
                        throw new ArgumentException($"wall '{s.Mark}': 'mark' replacement would also delete another wall or floor; batch rolled back");
                    action = "replaced";
                }
                else action = "created";

                var line = Line.CreateBound(
                    new XYZ(MmUnits.ToFt(s.Start.X), MmUnits.ToFt(s.Start.Y), r.Level.ProjectElevation),
                    new XYZ(MmUnits.ToFt(s.End.X), MmUnits.ToFt(s.End.Y), r.Level.ProjectElevation));
                var created = Wall.Create(doc, line, r.Type.Id, r.Level.Id, r.Height,
                    MmUnits.ToFt(s.BaseOffsetMm), s.Flip, s.Usage != StructuralWallUsage.NonBearing);
                ModelLookup.SetMark(created, s.Mark);
                Param(created, BuiltInParameter.WALL_KEY_REF_PARAM, s.Mark, "location_line").Set((int)s.LocationLine);
                created.StructuralUsage = s.Usage;
                if (r.Top != null)
                {
                    Param(created, BuiltInParameter.WALL_HEIGHT_TYPE, s.Mark, "top_level").Set(r.Top.Id);
                    Param(created, BuiltInParameter.WALL_TOP_OFFSET, s.Mark, "top_offset_mm").Set(MmUnits.ToFt(s.TopOffsetMm));
                }
                else
                {
                    Param(created, BuiltInParameter.WALL_HEIGHT_TYPE, s.Mark, "top_level").Set(ElementId.InvalidElementId);
                    Param(created, BuiltInParameter.WALL_USER_HEIGHT_PARAM, s.Mark, "height_mm").Set(r.Height);
                }
                // Geometry and constraint values are stale until regeneration after creation/parameter changes.
                doc.Regenerate();
                applied.Add(s.Mark, created);
                protectedIds.Add(created.Id);
                changed = true;
                results.Add(new { mark = s.Mark, id = created.Id.ToLong(), action });
            }

            if (changed)
            {
                doc.Regenerate();
                // Also check earlier walls after later creations: automatic joins must not spoil the spec.
                foreach (var r in resolved)
                    if (!Matches(applied[r.Spec.Mark], r.Type, r.Level, r.Top, r.Spec))
                        throw new InvalidOperationException($"wall '{r.Spec.Mark}': regenerated type/level/offsets/height/start/end/location_line/structural_usage/flip do not match the spec; batch rolled back");
            }
        });

        return new
        {
            document = doc.Title, transaction = "committed", walls = results,
            other_walls_untouched = untouched, revit_warnings = warnings,
        };
    }

    // ------------------------------------------------------------------ comparison

    private static bool Matches(Wall wall, WallType type, Level level, Level? top, WallSpec s)
    {
        if (wall.GetTypeId() != type.Id || ModelLookup.Mark(wall) != s.Mark || wall.Flipped != s.Flip
            || wall.StructuralUsage != s.Usage) return false;
        if (wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() != level.Id
            || wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() != (top?.Id ?? ElementId.InvalidElementId)
            || wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() != (int)s.LocationLine
            || (wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1)
                != (s.Usage != StructuralWallUsage.NonBearing)) return false;
        if (!SameLength(wall, BuiltInParameter.WALL_BASE_OFFSET, s.BaseOffsetMm)) return false;
        if (top != null)
        {
            if (!SameLength(wall, BuiltInParameter.WALL_TOP_OFFSET, s.TopOffsetMm)) return false;
        }
        else if (!SameLength(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM, s.HeightMm)) return false;

        if (wall.Location is not LocationCurve { Curve: Line line }) return false;
        // Endpoint order determines exterior/flip semantics; do not canonicalize a reversed line.
        return SamePoint(line.GetEndPoint(0), s.Start) && SamePoint(line.GetEndPoint(1), s.End);
    }

    private static bool SameLength(Wall wall, BuiltInParameter bip, double mm) =>
        wall.get_Parameter(bip) is { } p && Math.Abs(p.AsDouble() - MmUnits.ToFt(mm)) <= Tol;

    private static bool SamePoint(XYZ point, (double X, double Y) want) =>
        Math.Abs(point.X - MmUnits.ToFt(want.X)) <= Tol && Math.Abs(point.Y - MmUnits.ToFt(want.Y)) <= Tol;

    private static Parameter Param(Wall wall, BuiltInParameter bip, string mark, string field) =>
        wall.get_Parameter(bip) is { IsReadOnly: false } p ? p
            : throw new ArgumentException($"wall '{mark}': '{field}' is not editable on this wall type");

    // ------------------------------------------------------------------ params

    private static List<WallSpec> Parse(JsonElement p)
    {
        SpecReader.Obj(p);
        var specs = new List<WallSpec>();
        foreach (var w in SpecReader.Items(p, "walls", required: true))
        {
            var mark = Context($"at walls[{specs.Count}] (mark unavailable)", "mark",
                () => SpecReader.Str(SpecReader.Obj(w, "wall"), "mark"));
            specs.Add(Context(mark, "spec", () =>
            {
                var start = Point(w, "start");
                var end = Point(w, "end");
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (!double.IsFinite(length) || length < 1.0)
                    throw new ArgumentException("'start/end' must define a finite segment at least 1 mm long");
                AxisGuard.Segment(mark, start, end, "start/end");
                var top = SpecReader.OptStr(w, "top_level");
                var height = top == null ? SpecReader.Num(w, "height_mm") : 0;
                if (top == null && height <= 0) throw new ArgumentException("'height_mm' must be positive");
                var location = SpecReader.OptStr(w, "location_line") ?? "Center";
                var line = location switch
                {
                    "Center" => WallLocationLine.WallCenterline,
                    "CoreCenter" => WallLocationLine.CoreCenterline,
                    "FinishFaceExterior" => WallLocationLine.FinishFaceExterior,
                    "FinishFaceInterior" => WallLocationLine.FinishFaceInterior,
                    "CoreExterior" => WallLocationLine.CoreExterior,
                    "CoreInterior" => WallLocationLine.CoreInterior,
                    _ => throw new ArgumentException($"'location_line' has unknown value '{location}'"),
                };
                var usageName = SpecReader.OptStr(w, "structural_usage") ?? "NonBearing";
                var usage = usageName switch
                {
                    "NonBearing" => StructuralWallUsage.NonBearing,
                    "Bearing" => StructuralWallUsage.Bearing,
                    "Shear" => StructuralWallUsage.Shear,
                    "Combined" => StructuralWallUsage.Combined,
                    _ => throw new ArgumentException($"'structural_usage' has unknown value '{usageName}'"),
                };
                return new WallSpec(mark, SpecReader.Str(w, "type"), SpecReader.Str(w, "level"),
                    SpecReader.Num(w, "base_offset_mm", 0), top, SpecReader.Num(w, "top_offset_mm", 0),
                    height, start, end, line, usage, SpecReader.Bool(w, "flip", false));
            }));
        }
        if (specs.Count == 0) throw new ArgumentException("'walls' is empty");
        SpecReader.Unique(specs.Select(s => s.Mark), "wall mark");
        return specs;
    }

    private static (double X, double Y) Point(JsonElement w, string field)
    {
        if (!w.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 2
            || v[0].ValueKind != JsonValueKind.Number || v[1].ValueKind != JsonValueKind.Number
            || !double.IsFinite(v[0].GetDouble()) || !double.IsFinite(v[1].GetDouble()))
            throw new ArgumentException($"'{field}' must be [x,y] finite numbers in mm");
        return (v[0].GetDouble(), v[1].GetDouble());
    }

    private static T Context<T>(string mark, string field, Func<T> read)
    {
        try { return read(); }
        catch (ArgumentException ex)
        {
            Log.Warning(ex, "Invalid wall {Mark}, field {Field}", mark, field);
            throw new ArgumentException($"wall '{mark}': '{field}': {ex.Message}", ex);
        }
    }

    /// <summary>The spec spelling of a Revit wall location-line value, for independent read-back.</summary>
    internal static string LocationLineName(WallLocationLine line) => line switch
    {
        WallLocationLine.WallCenterline => "Center",
        WallLocationLine.CoreCenterline => "CoreCenter",
        _ => line.ToString(),
    };
}
