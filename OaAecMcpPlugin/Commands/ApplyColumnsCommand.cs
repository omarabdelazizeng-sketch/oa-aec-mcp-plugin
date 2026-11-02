using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using OaAecMcpPlugin.Services;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Places or updates columns from a spec, in ONE transaction (modeling plan step 6).
///     Idempotent: columns are matched by Mark within their category. A column whose Mark is in the
///     spec is updated in place (type, position, rotation, levels, offsets); a missing one is created.
///     Columns whose Mark is not in the spec are never touched (only counted).
///
///     Params:
///     {
///       "columns": [
///         { "mark": "C1-GF-01", "kind": "structural" | "architectural",
///           "family": "M_Concrete-Rectangular-Column", "type": "C 250x500",
///           "x_mm": 1234.5, "y_mm": 678.9, "rotation_deg": 0,
///           "base_level": "Ground Floor Level", "base_offset_mm": -150,
///           "top_level": "First Floor Level", "top_offset_mm": -100 }
///       ]
///     }
///     Keep batches to a few hundred columns so a request finishes inside the 30 s dispatch timeout.
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on Revit's main thread.
/// </summary>
public class ApplyColumnsCommand : ICommand
{
    public string Name => "apply_columns";

    private const double Tol = 1e-9;

    private sealed record ColumnSpec(string Mark, bool Structural, string Family, string Type, double X, double Y,
        double RotationDeg, string BaseLevel, double BaseOffset, string TopLevel, double TopOffset);

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null) throw new InvalidOperationException("No active Revit document");
        if (doc.IsFamilyDocument) throw new ArgumentException("apply_columns needs a project document");

        var specs = Parse(parameters);
        var lookup = new ModelLookup(doc);
        var results = new List<object>();
        var untouched = 0;

        var warnings = RevitTx.Run(doc, "OA-AEC-MCP: apply_columns", () =>
        {
            var existing = new Dictionary<string, FamilyInstance>(StringComparer.Ordinal);
            foreach (var cat in new[] { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns })
                foreach (var fi in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType()
                             .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                {
                    var mark = ModelLookup.Mark(fi);
                    if (mark.Length == 0) { untouched++; continue; }
                    if (!existing.TryAdd((cat == BuiltInCategory.OST_StructuralColumns ? "S:" : "A:") + mark, fi))
                        throw new ArgumentException($"the model already has two columns with Mark '{mark}' — fix duplicates first");
                }
            var wantedKeys = specs.Select(s => (s.Structural ? "S:" : "A:") + s.Mark).ToHashSet(StringComparer.Ordinal);
            untouched += existing.Keys.Count(k => !wantedKeys.Contains(k));

            foreach (var s in specs)
            {
                var category = s.Structural ? BuiltInCategory.OST_StructuralColumns : BuiltInCategory.OST_Columns;
                var symbol = lookup.GetSymbol(category, s.Family, s.Type);
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

                var baseLevel = lookup.GetLevel(s.BaseLevel);
                var topLevel = lookup.GetLevel(s.TopLevel);
                var bottomZ = MmUnits.FromFt(baseLevel.ProjectElevation) + s.BaseOffset;
                var topZ = MmUnits.FromFt(topLevel.ProjectElevation) + s.TopOffset;
                if (topZ - bottomZ < 1.0)
                    throw new ArgumentException($"column '{s.Mark}': top ({topZ:0.###} mm) is not above bottom ({bottomZ:0.###} mm)");

                var key = (s.Structural ? "S:" : "A:") + s.Mark;
                FamilyInstance column;
                var changes = new List<string>();

                if (existing.TryGetValue(key, out var found))
                {
                    column = found;
                    if (column.Symbol.Id != symbol.Id) { column.ChangeTypeId(symbol.Id); changes.Add("type"); }
                }
                else
                {
                    var at = new XYZ(MmUnits.ToFt(s.X), MmUnits.ToFt(s.Y), baseLevel.ProjectElevation);
                    column = doc.Create.NewFamilyInstance(at, symbol, baseLevel,
                        s.Structural ? StructuralType.Column : StructuralType.NonStructural);
                    ModelLookup.SetMark(column, s.Mark);
                    changes.Add("created");
                }

                // Levels first (top before base so the column never inverts), then offsets.
                SetId(column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLevel.Id, changes, "top level");
                SetId(column, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, baseLevel.Id, changes, "base level");
                SetLen(column, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, s.TopOffset, changes, "top offset");
                SetLen(column, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, s.BaseOffset, changes, "base offset");

                // Plan position and rotation.
                if (column.Location is not LocationPoint lp)
                    throw new InvalidOperationException($"column '{s.Mark}' has no point location");
                var dx = MmUnits.ToFt(s.X) - lp.Point.X;
                var dy = MmUnits.ToFt(s.Y) - lp.Point.Y;
                if (Math.Abs(dx) > Tol || Math.Abs(dy) > Tol)
                {
                    ElementTransformUtils.MoveElement(doc, column.Id, new XYZ(dx, dy, 0));
                    if (!changes.Contains("created")) changes.Add("position");
                }
                var lp2 = (LocationPoint)column.Location;
                var wantRot = s.RotationDeg * Math.PI / 180.0;
                var dRot = Math.IEEERemainder(wantRot - lp2.Rotation, 2 * Math.PI);
                if (Math.Abs(dRot) > 1e-9)
                {
                    var axis = Line.CreateBound(lp2.Point, lp2.Point + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, column.Id, axis, dRot);
                    if (!changes.Contains("created")) changes.Add("rotation");
                }

                var action = changes.Count == 0 ? "unchanged" : changes.Contains("created") ? "created" : "updated: " + string.Join(", ", changes);
                results.Add(new { mark = s.Mark, id = column.Id.Value, action });
            }
        });

        return new
        {
            document = doc.Title, transaction = "committed", columns = results,
            other_columns_untouched = untouched, revit_warnings = warnings,
        };
    }

    private static void SetId(Element e, BuiltInParameter bip, ElementId value, List<string> changes, string label)
    {
        var p = e.get_Parameter(bip) ?? throw new InvalidOperationException($"element {e.Id.Value} has no {label} parameter");
        if (p.AsElementId() == value) return;
        p.Set(value);
        if (!changes.Contains("created")) changes.Add(label);
    }

    private static void SetLen(Element e, BuiltInParameter bip, double mm, List<string> changes, string label)
    {
        var p = e.get_Parameter(bip) ?? throw new InvalidOperationException($"element {e.Id.Value} has no {label} parameter");
        var target = MmUnits.ToFt(mm);
        if (Math.Abs(p.AsDouble() - target) <= Tol) return;
        p.Set(target);
        if (!changes.Contains("created")) changes.Add(label);
    }

    private static List<ColumnSpec> Parse(JsonElement p)
    {
        SpecReader.Obj(p);
        var specs = new List<ColumnSpec>();
        foreach (var c in SpecReader.Items(p, "columns", required: true))
        {
            var kind = SpecReader.OptStr(c, "kind") ?? "structural";
            if (kind != "structural" && kind != "architectural")
                throw new ArgumentException($"column kind must be 'structural' or 'architectural', got '{kind}'");
            specs.Add(new ColumnSpec(
                SpecReader.Str(c, "mark"), kind == "structural",
                SpecReader.Str(c, "family"), SpecReader.Str(c, "type"),
                SpecReader.Num(c, "x_mm"), SpecReader.Num(c, "y_mm"), SpecReader.Num(c, "rotation_deg", 0),
                SpecReader.Str(c, "base_level"), SpecReader.Num(c, "base_offset_mm", 0),
                SpecReader.Str(c, "top_level"), SpecReader.Num(c, "top_offset_mm", 0)));
        }
        if (specs.Count == 0) throw new ArgumentException("'columns' is empty");
        SpecReader.Unique(specs.Select(s => (s.Structural ? "S:" : "A:") + s.Mark), "column mark");
        return specs;
    }
}
