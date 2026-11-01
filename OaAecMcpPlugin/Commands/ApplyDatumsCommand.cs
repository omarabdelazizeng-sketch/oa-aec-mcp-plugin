using Autodesk.Revit.DB;
using Serilog;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Creates or updates the project datum, levels and grids from a JSON spec, in ONE transaction.
///
///     Idempotent: levels and grids are matched by name. Re-running the same spec changes nothing
///     (every item reports "unchanged"). Elements that are not in the spec are left in place and
///     reported as warnings — this command never deletes anything it does not own by name.
///
///     Order matters and is fixed: datum → levels → views → grids.
///     Revit holds level elevations relative to the Project Base Point, so the PBP must be settled
///     before any level elevation is written (a level set first would land at the wrong height).
///
///     Datum: the PBP is kept at the internal origin; the requested shared coordinates are applied
///     to the internal origin with ProjectLocation.SetProjectPosition (this moves the Survey Point,
///     not the model). Setting BASEPOINT_ELEVATION_PARAM is deliberately NOT used — it moves the PBP.
///
///     Revit warnings raised by the transaction are captured (and returned) instead of showing a
///     modal dialog, which would block the main thread. Revit errors roll the whole transaction back.
///
///     Params (all optional, at least one of datum/levels/grids required):
///     {
///       "datum":  { "x_mm": 0, "y_mm": 0, "elevation_mm": 213200, "angle_to_true_north_deg": 0 },
///       "levels": [ { "name": "Ground Floor Level", "elevation_mm": 150 } ],
///       "grids":  [ { "name": "A", "start_mm": [x, y], "end_mm": [x, y] } ],
///       "create_floor_plans": true
///     }
///     Level elevations are relative to the internal origin (= the PBP, since the PBP is kept there).
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on Revit's main thread.
/// </summary>
public class ApplyDatumsCommand : ICommand
{
    public string Name => "apply_datums";

    private const double SameTolFeet = 1e-9;

    private static double Ft(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    private static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

    private sealed record DatumSpec(double XMm, double YMm, double ElevationMm, double AngleDeg);
    private sealed record LevelSpec(string Name, double ElevationMm);
    private sealed record GridSpec(string Name, double X0, double Y0, double X1, double Y1);
    private sealed record Spec(DatumSpec? Datum, List<LevelSpec> Levels, List<GridSpec> Grids, bool CreateFloorPlans);

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null)
            throw new InvalidOperationException("No active Revit document");
        if (doc.IsFamilyDocument)
            throw new ArgumentException("apply_datums needs a project document, not a family document");

        var spec = Parse(parameters);
        var report = new Report();
        var failures = new CollectingFailures();

        using var tx = new Transaction(doc, "OA-AEC-MCP: apply_datums");
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(failures);
        options.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(options);

        tx.Start();
        try
        {
            if (spec.Datum != null) ApplyDatum(doc, spec.Datum, report);
            if (spec.Levels.Count > 0) ApplyLevels(doc, spec.Levels, spec.CreateFloorPlans, report);
            if (spec.Grids.Count > 0) ApplyGrids(doc, spec.Grids, report);

            var status = tx.Commit();
            if (status != TransactionStatus.Committed)
                throw new InvalidOperationException(
                    $"apply_datums transaction ended with status {status}; Revit errors: {string.Join(" | ", failures.Errors)}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "apply_datums failed; rolling back");
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            throw;
        }

        Log.Information("apply_datums committed: {Levels} levels, {Grids} grids, {Warnings} Revit warnings",
            report.Levels.Count, report.Grids.Count, failures.Warnings.Count);

        return new
        {
            document = doc.Title,
            transaction = "committed",
            datum = report.Datum,
            levels = report.Levels,
            views = report.Views,
            grids = report.Grids,
            warnings = report.Warnings,
            revit_warnings = failures.Warnings,
        };
    }

    // ------------------------------------------------------------------ datum

    private static void ApplyDatum(Document doc, DatumSpec d, Report report)
    {
        var pbp = BasePoint.GetProjectBasePoint(doc);
        var before = pbp.Position;
        if (!before.IsAlmostEqualTo(XYZ.Zero, SameTolFeet))
        {
            ElementTransformUtils.MoveElement(doc, pbp.Id, XYZ.Zero - before);
            report.Warnings.Add(
                $"Project Base Point moved back to the internal origin from ({Mm(before.X):0.####}, {Mm(before.Y):0.####}, {Mm(before.Z):0.####}) mm");
        }

        var current = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
        var target = new ProjectPosition(Ft(d.XMm), Ft(d.YMm), Ft(d.ElevationMm), d.AngleDeg * Math.PI / 180.0);
        var same = Math.Abs(current.EastWest - target.EastWest) < SameTolFeet
                   && Math.Abs(current.NorthSouth - target.NorthSouth) < SameTolFeet
                   && Math.Abs(current.Elevation - target.Elevation) < SameTolFeet
                   && Math.Abs(current.Angle - target.Angle) < 1e-12;
        if (!same) doc.ActiveProjectLocation.SetProjectPosition(XYZ.Zero, target);

        report.Datum = new
        {
            action = same ? "unchanged" : "updated",
            internal_origin_shared_mm = new[] { d.XMm, d.YMm, d.ElevationMm },
            angle_to_true_north_deg = d.AngleDeg,
        };
    }

    // ------------------------------------------------------------------ levels

    private static void ApplyLevels(Document doc, List<LevelSpec> specs, bool createPlans, Report report)
    {
        var existing = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.ProjectElevation).ToList();
        var wanted = specs.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var byName = existing.ToDictionary(l => l.Name, StringComparer.Ordinal);
        var oldNames = existing.ToDictionary(l => l.Id, l => l.Name);

        // Levels not in the spec form a pool that is reused (renamed) before new levels are created,
        // so template levels and their views are kept instead of piling up.
        var pool = existing.Where(l => !wanted.Contains(l.Name)).ToList();
        foreach (var l in pool) l.Name = $"tmp_oa_{l.Id.Value}";

        var made = new List<(LevelSpec Spec, Level Level)>();
        foreach (var s in specs)
        {
            Level level;
            string action;
            if (byName.TryGetValue(s.Name, out var found))
            {
                level = found;
                action = Math.Abs(Mm(found.ProjectElevation) - s.ElevationMm) < 1e-6 ? "unchanged" : "elevation updated";
            }
            else if (pool.Count > 0)
            {
                level = pool[0];
                pool.RemoveAt(0);
                action = $"reused '{oldNames[level.Id]}'";
            }
            else
            {
                level = Level.Create(doc, Ft(s.ElevationMm));
                action = "created";
            }

            level.Elevation = Ft(s.ElevationMm);
            // Level.Elevation is relative to the level type's elevation base; correct against the
            // internal elevation so the result is right whatever that base is.
            var dev = s.ElevationMm - Mm(level.ProjectElevation);
            if (Math.Abs(dev) > 1e-6) level.Elevation += Ft(dev);
            level.Name = s.Name;

            made.Add((s, level));
            report.Levels.Add(new { name = s.Name, id = level.Id.Value, action, internal_elevation_mm = Mm(level.ProjectElevation) });
        }

        foreach (var l in pool)
        {
            l.Name = oldNames[l.Id];
            report.Warnings.Add($"Level '{oldNames[l.Id]}' is not in the spec and was left in place");
        }

        ApplyViews(doc, made, oldNames, createPlans, report);
    }

    private static void ApplyViews(Document doc, List<(LevelSpec Spec, Level Level)> made,
        Dictionary<ElementId, string> oldNames, bool createPlans, Report report)
    {
        var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate).ToList();
        var floorPlanType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);

        foreach (var (spec, level) in made)
        {
            var oldName = oldNames.TryGetValue(level.Id, out var n) ? n : null;
            var mine = plans.Where(v => v.GenLevel != null && v.GenLevel.Id == level.Id).ToList();

            // Template views follow the old level name ("L1 - Architectural") — carry the new name over.
            if (oldName != null && oldName != spec.Name)
            {
                foreach (var v in mine.Where(v => v.Name.StartsWith(oldName, StringComparison.Ordinal)))
                {
                    var newName = spec.Name + v.Name.Substring(oldName.Length);
                    try { v.Name = newName; }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                    {
                        Log.Warning(ex, "apply_datums: could not rename view {View} to {NewName}", v.Name, newName);
                        report.Warnings.Add($"View '{v.Name}' could not be renamed to '{newName}': {ex.Message}");
                    }
                }
            }

            if (createPlans && floorPlanType != null && !mine.Any(v => v.ViewType == ViewType.FloorPlan))
            {
                var plan = ViewPlan.Create(doc, floorPlanType.Id, level.Id);
                try { plan.Name = spec.Name; }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    Log.Warning(ex, "apply_datums: could not name new floor plan {Name}", spec.Name);
                    report.Warnings.Add($"New floor plan for '{spec.Name}' kept the default name: {ex.Message}");
                }
                mine.Add(plan);
            }

            report.Views.Add(new
            {
                level = spec.Name,
                floor_plans = mine.Where(v => v.ViewType == ViewType.FloorPlan).Select(v => v.Name).ToList(),
            });
        }
    }

    // ------------------------------------------------------------------ grids

    private static void ApplyGrids(Document doc, List<GridSpec> specs, Report report)
    {
        var have = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
            .ToDictionary(g => g.Name, StringComparer.Ordinal);
        var wanted = specs.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var g in have.Values.Where(g => !wanted.Contains(g.Name)))
            report.Warnings.Add($"Grid '{g.Name}' is not in the spec and was left in place");

        var toName = new List<(Grid Grid, string Name, string Action)>();
        foreach (var s in specs)
        {
            var a = new XYZ(Ft(s.X0), Ft(s.Y0), 0);
            var b = new XYZ(Ft(s.X1), Ft(s.Y1), 0);
            var action = "created";

            if (have.TryGetValue(s.Name, out var existing))
            {
                if (existing.Curve is Line line && !existing.IsCurved && SameSegment(line, a, b))
                {
                    report.Grids.Add(new { name = s.Name, id = existing.Id.Value, action = "unchanged" });
                    continue;
                }
                // A grid's 3D line cannot be edited in place; replace it.
                doc.Delete(existing.Id);
                action = "replaced";
            }

            var grid = Grid.Create(doc, Line.CreateBound(a, b));
            grid.Name = $"tmp_oa_{grid.Id.Value}";   // final names after all are created — avoids clashes with auto-names
            toName.Add((grid, s.Name, action));
        }

        foreach (var (grid, name, action) in toName)
        {
            grid.Name = name;
            report.Grids.Add(new { name, id = grid.Id.Value, action });
        }
    }

    private static bool SameSegment(Line line, XYZ a, XYZ b)
    {
        var p = line.GetEndPoint(0);
        var q = line.GetEndPoint(1);
        return (p.IsAlmostEqualTo(a, SameTolFeet) && q.IsAlmostEqualTo(b, SameTolFeet))
               || (p.IsAlmostEqualTo(b, SameTolFeet) && q.IsAlmostEqualTo(a, SameTolFeet));
    }

    // ------------------------------------------------------------------ params

    private static Spec Parse(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("params must be an object with datum, levels and/or grids");

        DatumSpec? datum = null;
        if (p.TryGetProperty("datum", out var d) && d.ValueKind == JsonValueKind.Object)
            datum = new DatumSpec(Num(d, "x_mm", 0), Num(d, "y_mm", 0), Num(d, "elevation_mm", null),
                Num(d, "angle_to_true_north_deg", 0));

        var levels = new List<LevelSpec>();
        if (p.TryGetProperty("levels", out var ls) && ls.ValueKind == JsonValueKind.Array)
            foreach (var l in ls.EnumerateArray())
                levels.Add(new LevelSpec(Str(l, "name"), Num(l, "elevation_mm", null)));

        var grids = new List<GridSpec>();
        if (p.TryGetProperty("grids", out var gs) && gs.ValueKind == JsonValueKind.Array)
            foreach (var g in gs.EnumerateArray())
            {
                var (x0, y0) = Pt(g, "start_mm");
                var (x1, y1) = Pt(g, "end_mm");
                var name = Str(g, "name");
                if (Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) < 1.0)
                    throw new ArgumentException($"grid '{name}' is shorter than 1 mm");
                grids.Add(new GridSpec(name, x0, y0, x1, y1));
            }

        var createPlans = !p.TryGetProperty("create_floor_plans", out var cp) || cp.ValueKind != JsonValueKind.False;

        if (datum == null && levels.Count == 0 && grids.Count == 0)
            throw new ArgumentException("nothing to apply: give datum, levels and/or grids");
        Unique(levels.Select(l => l.Name), "level");
        Unique(grids.Select(g => g.Name), "grid");

        return new Spec(datum, levels, grids, createPlans);
    }

    private static void Unique(IEnumerable<string> names, string kind)
    {
        var dup = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new ArgumentException($"duplicate {kind} name '{dup.Key}'");
    }

    private static string Str(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
            throw new ArgumentException($"'{key}' must be a non-empty string");
        return v.GetString()!.Trim();
    }

    private static double Num(JsonElement e, string key, double? fallback)
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

    private static (double X, double Y) Pt(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 2)
            throw new ArgumentException($"'{key}' must be [x, y] in mm");
        var x = v[0];
        var y = v[1];
        if (x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number)
            throw new ArgumentException($"'{key}' must contain numbers");
        return (x.GetDouble(), y.GetDouble());
    }

    // ------------------------------------------------------------------ support

    private sealed class Report
    {
        public object? Datum { get; set; }
        public List<object> Levels { get; } = new();
        public List<object> Views { get; } = new();
        public List<object> Grids { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    ///     Collects Revit warnings (and deletes them so no modal dialog blocks the main thread);
    ///     any error-level failure rolls the transaction back and is reported.
    /// </summary>
    private sealed class CollectingFailures : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var hasError = false;
            foreach (var f in accessor.GetFailureMessages())
            {
                var text = f.GetDescriptionText();
                if (f.GetSeverity() == FailureSeverity.Warning)
                {
                    Warnings.Add(text);
                    accessor.DeleteWarning(f);
                }
                else
                {
                    Errors.Add(text);
                    hasError = true;
                }
            }
            if (hasError) Log.Error("apply_datums: Revit errors, rolling back: {Errors}", string.Join(" | ", Errors));
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
