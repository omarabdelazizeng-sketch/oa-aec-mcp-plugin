using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using Autodesk.Revit.DB.Structure;
using OaAecMcpPlugin.Services;
using Serilog;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>Shared wall-hosted placement, replacement and regenerated verification for doors/windows.</summary>
internal static class ApplyOpenings
{
    private const double Tol = 1e-9; // internal feet, as in apply_walls

    private sealed record OpeningSpec(string Mark, string Family, string Type, string HostMark,
        (double X, double Y) Position, string? Level, double? SillMm, bool Hand, bool Facing);

    private sealed record Resolved(OpeningSpec Spec, FamilySymbol Symbol, Wall Host, Level Level, XYZ Point);

    /// <summary>Called only by ICommand.Execute on Revit's main thread; one transaction for the entire batch.</summary>
    internal static object Execute(Document? doc, JsonElement parameters, string key, BuiltInCategory category)
    {
        if (doc == null) throw new InvalidOperationException("No active Revit document");
        if (doc.IsFamilyDocument) throw new ArgumentException($"apply_{key} needs a project document");
        var specs = Parse(parameters, key);
        var lookup = new ModelLookup(doc);
        var hosts = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
            .GroupBy(ModelLookup.Mark, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var validationWarnings = new List<string>();

        // Resolve and validate the complete request before changing anything.
        var resolved = specs.Select(s => Context(key, s.Mark, () =>
        {
            if (!hosts.TryGetValue(s.HostMark, out var matches) || matches.Count == 0)
                throw new ArgumentException($"host_wall_mark '{s.HostMark}' not found");
            if (matches.Count != 1)
                throw new ArgumentException($"host_wall_mark '{s.HostMark}' matches several walls; fix duplicate Marks first");
            var host = matches[0];
            var symbol = lookup.GetSymbol(category, s.Family, s.Type);
            if (symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBasedHosted)
                throw new ArgumentException($"family '{s.Family}' is not a level-based hosted family; wall hosting is required");
            var level = s.Level != null ? lookup.GetLevel(s.Level)
                : doc.GetElement(host.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId()
                                 ?? ElementId.InvalidElementId) as Level
                  ?? throw new ArgumentException($"host_wall_mark '{s.HostMark}' has no base level");
            var curve = (host.Location as LocationCurve)?.Curve
                        ?? throw new ArgumentException($"host_wall_mark '{s.HostMark}' has no location curve");
            var point = Project(curve, s.Position);
            var width = OpeningFacts.Dimension(symbol, width: true);
            if (width is > 0)
            {
                if (curve.Length + Tol < width.Value)
                    throw new ArgumentException($"host_wall_mark '{s.HostMark}' is too short ({MmUnits.FromFt(curve.Length):0.###} mm) for opening width {MmUnits.FromFt(width.Value):0.###} mm");
            }
            else validationWarnings.Add($"{key} '{s.Mark}': opening width is unreadable/nonpositive on the symbol; wall-length check skipped");
            return new Resolved(s, symbol, host, level, new XYZ(point.X, point.Y, level.ProjectElevation));
        })).ToList();

        var instances = new FilteredElementCollector(doc).OfCategory(category).OfClass(typeof(FamilyInstance))
            .WhereElementIsNotElementType().Cast<FamilyInstance>().ToList();
        var existing = new Dictionary<string, FamilyInstance>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            var mark = ModelLookup.Mark(instance);
            if (mark.Length != 0 && !existing.TryAdd(mark, instance))
                throw new ArgumentException($"{key} '{mark}': duplicate Mark in the model; fix duplicates first");
        }
        var wanted = specs.Select(s => s.Mark).ToHashSet(StringComparer.Ordinal);
        var untouched = instances.Count(i => !wanted.Contains(ModelLookup.Mark(i)));
        var results = new List<object>();
        var applied = new Dictionary<string, FamilyInstance>(StringComparer.Ordinal);
        // Guard against cascading deletion of another opening or a host, including other categories/Marks.
        var protectedIds = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(new[]
            { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors }))
            .WhereElementIsNotElementType().ToElementIds().ToHashSet();

        var warnings = RevitTx.Run(doc, $"OA-AEC-MCP: apply_{key}", () =>
        {
            var changed = false;
            foreach (var r in resolved)
                Context(key, r.Spec.Mark, () =>
                {
                    var s = r.Spec;
                    string action;
                    if (existing.TryGetValue(s.Mark, out var found))
                    {
                        if (Matches(found, r))
                        {
                            applied.Add(s.Mark, found);
                            results.Add(new { mark = s.Mark, id = found.Id.ToLong(), action = "unchanged" });
                            return true;
                        }
                        var oldId = found.Id;
                        if (doc.Delete(oldId).Any(id => id != oldId && protectedIds.Contains(id)))
                            throw new InvalidOperationException("replacement would delete another door, window, wall or floor; batch rolled back");
                        action = "replaced";
                    }
                    else action = "created";

                    if (!r.Symbol.IsActive) { r.Symbol.Activate(); doc.Regenerate(); }
                    var created = doc.Create.NewFamilyInstance(r.Point, r.Symbol, r.Host, r.Level, StructuralType.NonStructural)
                                  ?? throw new InvalidOperationException("Revit could not create the wall-hosted instance");
                    ModelLookup.SetMark(created, s.Mark);
                    doc.Regenerate();
                    if (s.SillMm.HasValue)
                    {
                        var desired = MmUnits.ToFt(s.SillMm.Value);
                        var current = OpeningFacts.Length(created, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        // Set may report false when the value already agrees (not a failed placement).
                        if (!current.HasValue || Math.Abs(current.Value - desired) > Tol)
                        {
                            var sill = created.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                            if (sill is not { IsReadOnly: false, StorageType: StorageType.Double }
                                || !sill.Set(desired))
                                throw new InvalidOperationException("sill_height_mm is not editable or Revit rejected it");
                            doc.Regenerate();
                        }
                    }
                    if (created.HandFlipped != s.Hand && (!created.CanFlipHand || !created.flipHand()))
                        throw new InvalidOperationException("family cannot reach the requested flip_hand state");
                    doc.Regenerate();
                    if (created.FacingFlipped != s.Facing && (!created.CanFlipFacing || !created.flipFacing()))
                        throw new InvalidOperationException("family cannot reach the requested flip_facing state");
                    doc.Regenerate();
                    applied.Add(s.Mark, created);
                    protectedIds.Add(created.Id);
                    results.Add(new { mark = s.Mark, id = created.Id.ToLong(), action });
                    changed = true;
                    return true;
                });

            if (changed)
            {
                doc.Regenerate();
                foreach (var r in resolved)
                    if (!Matches(applied[r.Spec.Mark], r))
                        throw new InvalidOperationException($"{key} '{r.Spec.Mark}': regenerated Mark/type/host/level/position/sill/flips do not match; batch rolled back");
            }
        });
        warnings.AddRange(validationWarnings);
        return new Dictionary<string, object>
        {
            ["document"] = doc.Title, ["transaction"] = "committed", [key] = results,
            [$"other_{key}_untouched"] = untouched, ["revit_warnings"] = warnings,
        };
    }

    private static bool Matches(FamilyInstance instance, Resolved r) =>
        instance.IsValidObject && instance.GetTypeId() == r.Symbol.Id && instance.Host?.Id == r.Host.Id
        && instance.LevelId == r.Level.Id && ModelLookup.Mark(instance) == r.Spec.Mark
        && instance.Location is LocationPoint lp
        && Math.Abs(lp.Point.X - r.Point.X) <= Tol && Math.Abs(lp.Point.Y - r.Point.Y) <= Tol
        && instance.HandFlipped == r.Spec.Hand && instance.FacingFlipped == r.Spec.Facing
        && (!r.Spec.SillMm.HasValue
            || OpeningFacts.Length(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) is { } sill
            && Math.Abs(sill - MmUnits.ToFt(r.Spec.SillMm.Value)) <= Tol);

    private static XYZ Project(Curve curve, (double X, double Y) position)
    {
        if (!curve.IsBound) throw new ArgumentException("host wall location curve must be bounded");
        var a = curve.GetEndPoint(0);
        var b = curve.GetEndPoint(1);
        var mid = curve.Evaluate(0.5, true);
        if (Math.Abs(a.Z - b.Z) > Tol || Math.Abs(a.Z - mid.Z) > Tol)
            throw new ArgumentException("host wall location curve must be horizontal for a plan position");
        var input = new XYZ(MmUnits.ToFt(position.X), MmUnits.ToFt(position.Y), a.Z);
        var nearest = input.DistanceTo(a) <= input.DistanceTo(b) ? a : b;
        // Every arc point is equidistant from its centre; choose the nearest endpoint deterministically.
        if (curve is not Arc arc || input.DistanceTo(arc.Center) > Tol)
        {
            var projection = curve.Project(input)
                             ?? throw new ArgumentException("position cannot be projected onto the host wall");
            var t = curve.ComputeNormalizedParameter(projection.Parameter);
            if (t >= 0 && t <= 1 && input.DistanceTo(projection.XYZPoint) < input.DistanceTo(nearest))
                nearest = projection.XYZPoint;
        }
        if (input.DistanceTo(nearest) > MmUnits.ToFt(200) + Tol)
            throw new ArgumentException("position is farther than 200 mm from the host wall's bounded location curve");
        return nearest;
    }

    private static List<OpeningSpec> Parse(JsonElement parameters, string key)
    {
        SpecReader.Obj(parameters);
        var specs = new List<OpeningSpec>();
        foreach (var item in SpecReader.Items(parameters, key, required: true))
        {
            SpecReader.Obj(item, $"{key}[{specs.Count}]");
            var mark = SpecReader.Str(item, "mark");
            specs.Add(Context(key, mark, () =>
            {
                if (!item.TryGetProperty("position", out var p) || p.ValueKind != JsonValueKind.Array
                    || p.GetArrayLength() != 2 || p[0].ValueKind != JsonValueKind.Number
                    || p[1].ValueKind != JsonValueKind.Number || !SpecReader.IsFinite(p[0].GetDouble())
                    || !SpecReader.IsFinite(p[1].GetDouble()))
                    throw new ArgumentException("'position' must be [x,y] finite numbers in mm");
                double? sill = key == "doors" ? 0 : null;
                if (item.TryGetProperty("sill_height_mm", out var value) && value.ValueKind != JsonValueKind.Null)
                    sill = SpecReader.Num(item, "sill_height_mm");
                return new OpeningSpec(mark, SpecReader.Str(item, "family"), SpecReader.Str(item, "type"),
                    SpecReader.Str(item, "host_wall_mark"), (p[0].GetDouble(), p[1].GetDouble()),
                    SpecReader.OptStr(item, "level"), sill, SpecReader.Bool(item, "flip_hand", false),
                    SpecReader.Bool(item, "flip_facing", false));
            }));
        }
        if (specs.Count == 0) throw new ArgumentException($"'{key}' is empty");
        SpecReader.Unique(specs.Select(s => s.Mark), $"{key} mark");
        return specs;
    }

    private static T Context<T>(string key, string mark, Func<T> body)
    {
        try { return body(); }
        catch (Exception ex)
        {
            Log.Error(ex, "Invalid or failed {Category} placement for {Mark}", key, mark);
            var message = $"{key} '{mark}': {ex.Message}";
            if (ex is ArgumentException) throw new ArgumentException(message, ex);
            throw new InvalidOperationException(message, ex);
        }
    }
}
