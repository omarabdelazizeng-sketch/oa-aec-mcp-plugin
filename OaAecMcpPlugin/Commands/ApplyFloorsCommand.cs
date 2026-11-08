using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using OaAecMcpPlugin.Services;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Creates or updates floors (slabs) from boundary polygons, in ONE transaction (modeling plan step 6).
///     Idempotent: floors are matched by Mark. If type, level, offset, structural flag and sketch loops
///     all match, the floor is left alone; otherwise it is replaced (deleted and recreated with the same Mark),
///     because a floor's sketch cannot be edited reliably in place. Floors whose Mark is not in the spec
///     are never touched (only counted).
///
///     Params:
///     {
///       "floors": [
///         { "mark": "SL-1F-01", "type": "Slab 200", "level": "First Floor Level", "offset_mm": -100,
///           "structural": true,
///           "boundary": [[x,y], ...],               // outer loop, mm, straight segments
///           "openings": [ [[x,y], ...], ... ] }     // optional inner loops (stairs, shafts)
///       ]
///     }
///     Arcs are not supported yet — curved edges must arrive as polylines.
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on Revit's main thread.
/// </summary>
public class ApplyFloorsCommand : ICommand
{
    public string Name => "apply_floors";

    private sealed record FloorSpec(string Mark, string Type, string Level, double OffsetMm, bool Structural,
        List<(double X, double Y)> Boundary, List<List<(double X, double Y)>> Openings);

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null) throw new InvalidOperationException("No active Revit document");
        if (doc.IsFamilyDocument) throw new ArgumentException("apply_floors needs a project document");

        var specs = Parse(parameters);
        var lookup = new ModelLookup(doc);
        var results = new List<object>();
        var untouched = 0;

        var warnings = RevitTx.Run(doc, "OA-AEC-MCP: apply_floors", () =>
        {
            var existing = new Dictionary<string, Floor>(StringComparer.Ordinal);
            foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
            {
                var mark = ModelLookup.Mark(f);
                if (mark.Length == 0) { untouched++; continue; }
                if (!existing.TryAdd(mark, f))
                    throw new ArgumentException($"the model already has two floors with Mark '{mark}' — fix duplicates first");
            }
            var wanted = specs.Select(s => s.Mark).ToHashSet(StringComparer.Ordinal);
            untouched += existing.Keys.Count(k => !wanted.Contains(k));

            foreach (var s in specs)
            {
                var type = lookup.GetHostType<FloorType>(s.Type);
                var level = lookup.GetLevel(s.Level);
                var wantLoops = new List<List<(double X, double Y)>> { s.Boundary };
                wantLoops.AddRange(s.Openings);

                string action;
                if (existing.TryGetValue(s.Mark, out var floor))
                {
                    if (Matches(doc, floor, type, level, s, wantLoops))
                    {
                        results.Add(new { mark = s.Mark, id = floor.Id.ToLong(), action = "unchanged" });
                        continue;
                    }
                    doc.Delete(floor.Id);
                    action = "replaced";
                }
                else action = "created";

                var loops = new List<CurveLoop> { ToLoop(s.Boundary, counterClockwise: true) };
                loops.AddRange(s.Openings.Select(o => ToLoop(o, counterClockwise: false)));
                var created = Floor.Create(doc, loops, type.Id, level.Id, s.Structural, null, 0.0);
                ModelLookup.SetMark(created, s.Mark);
                created.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(MmUnits.ToFt(s.OffsetMm));

                results.Add(new { mark = s.Mark, id = created.Id.ToLong(), action });
            }
        });

        return new
        {
            document = doc.Title, transaction = "committed", floors = results,
            other_floors_untouched = untouched, revit_warnings = warnings,
        };
    }

    // ------------------------------------------------------------------ comparison

    private static bool Matches(Document doc, Floor floor, FloorType type, Level level, FloorSpec s,
        List<List<(double X, double Y)>> wantLoops)
    {
        if (floor.GetTypeId() != type.Id || floor.LevelId != level.Id) return false;
        var off = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;
        if (Math.Abs(off - MmUnits.ToFt(s.OffsetMm)) > 1e-9) return false;
        var structural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;
        if (structural != s.Structural) return false;

        if (doc.GetElement(floor.SketchId) is not Sketch sketch) return false;
        var haveLoops = new List<List<(double X, double Y)>>();
        foreach (CurveArray arr in sketch.Profile)
        {
            var pts = new List<(double X, double Y)>();
            foreach (Curve c in arr)
            {
                if (c is not Line) return false;          // spec loops are straight segments only
                var p = c.GetEndPoint(0);
                pts.Add((MmUnits.FromFt(p.X), MmUnits.FromFt(p.Y)));
            }
            haveLoops.Add(pts);
        }
        return Canonical(haveLoops).SequenceEqual(Canonical(wantLoops));
    }

    /// <summary>Order-independent form of a set of loops: each loop rotated to its smallest point,
    /// in its smaller direction, rounded to 0.001 mm; loops sorted.</summary>
    private static List<string> Canonical(List<List<(double X, double Y)>> loops) =>
        loops.Select(loop =>
        {
            var r = loop.Select(p => (Math.Round(p.X, 3), Math.Round(p.Y, 3))).ToList();
            string Key(List<(double, double)> l)
            {
                var i = l.IndexOf(l.Min());
                return string.Join(";", l.Skip(i).Concat(l.Take(i)).Select(q => $"{q.Item1:0.###},{q.Item2:0.###}"));
            }
            var fwd = Key(r);
            var rev = Key(Enumerable.Reverse(r).ToList());
            return string.CompareOrdinal(fwd, rev) <= 0 ? fwd : rev;
        }).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static CurveLoop ToLoop(List<(double X, double Y)> pts, bool counterClockwise)
    {
        var ordered = SpecReader.SignedArea(pts) > 0 == counterClockwise ? pts : Enumerable.Reverse(pts).ToList();
        var curves = new List<Curve>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];
            var b = ordered[(i + 1) % ordered.Count];
            curves.Add(Line.CreateBound(MmUnits.Xyz(a.X, a.Y), MmUnits.Xyz(b.X, b.Y)));
        }
        return CurveLoop.Create(curves);
    }

    // ------------------------------------------------------------------ params

    private static List<FloorSpec> Parse(JsonElement p)
    {
        SpecReader.Obj(p);
        var specs = new List<FloorSpec>();
        foreach (var f in SpecReader.Items(p, "floors", required: true))
        {
            var mark = SpecReader.Str(f, "mark");
            if (!f.TryGetProperty("boundary", out var b))
                throw new ArgumentException($"floor '{mark}': 'boundary' is required");
            var openings = SpecReader.Items(f, "openings")
                .Select((o, i) => SpecReader.Polygon(o, $"floor '{mark}' opening {i}")).ToList();
            var boundary = SpecReader.Polygon(b, $"floor '{mark}' boundary");
            foreach (var loop in new[] { boundary }.Concat(openings))
                for (var i = 0; i < loop.Count; i++)
                    AxisGuard.Segment(mark, loop[i], loop[(i + 1) % loop.Count]);
            specs.Add(new FloorSpec(mark, SpecReader.Str(f, "type"), SpecReader.Str(f, "level"),
                SpecReader.Num(f, "offset_mm", 0), SpecReader.Bool(f, "structural", false),
                boundary, openings));
        }
        if (specs.Count == 0) throw new ArgumentException("'floors' is empty");
        SpecReader.Unique(specs.Select(s => s.Mark), "floor mark");
        return specs;
    }
}
