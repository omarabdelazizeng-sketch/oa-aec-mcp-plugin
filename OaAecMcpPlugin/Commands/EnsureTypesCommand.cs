using Autodesk.Revit.DB;
using OaAecMcpPlugin.Services;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Creates or updates element types from a spec, in ONE transaction (modeling plan step 4).
///     Idempotent: types are matched by name; existing types are updated only where they differ.
///     Never deletes types.
///
///     Params:
///     {
///       "types": [
///         { "kind": "structural_column" | "column", "family": "M_Concrete-Rectangular-Column",
///           "name": "C 250x500", "base_type": "300 x 450mm" (optional, default = first type of the family),
///           "params_mm": { "b": 250, "h": 500 } },
///         { "kind": "floor" | "wall", "name": "Slab 200", "base_type": "Generic 150mm",
///           "layers": [ { "function": "Structure", "width_mm": 200, "material": "Concrete" } ] }
///       ]
///     }
///     params_mm values are length parameters of the family type, in mm.
///     layers replace the type's compound structure (functions: Structure, Substrate, Insulation,
///     Finish1, Finish2, Membrane, StructuralDeck). Walls must be Basic walls.
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on Revit's main thread.
/// </summary>
public class EnsureTypesCommand : ICommand
{
    public string Name => "ensure_types";

    private sealed record LayerSpec(MaterialFunctionAssignment Function, double WidthMm, string? Material);

    private sealed record TypeSpec(string Kind, string Name, string? Family, string? BaseType,
        Dictionary<string, double> ParamsMm, List<LayerSpec> Layers);

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null) throw new InvalidOperationException("No active Revit document");
        if (doc.IsFamilyDocument) throw new ArgumentException("ensure_types needs a project document");

        var specs = Parse(parameters);
        var lookup = new ModelLookup(doc);
        var results = new List<object>();

        var warnings = RevitTx.Run(doc, "OA-AEC-MCP: ensure_types", () =>
        {
            foreach (var s in specs)
            {
                results.Add(s.Kind switch
                {
                    "structural_column" => EnsureSymbol(doc, BuiltInCategory.OST_StructuralColumns, s),
                    "column" => EnsureSymbol(doc, BuiltInCategory.OST_Columns, s),
                    "floor" => EnsureHostType<FloorType>(doc, lookup, s),
                    "wall" => EnsureHostType<WallType>(doc, lookup, s),
                    _ => throw new ArgumentException($"unknown kind '{s.Kind}'"),
                });
            }
        });

        return new { document = doc.Title, transaction = "committed", types = results, revit_warnings = warnings };
    }

    // ------------------------------------------------------------------ family types (columns)

    private static object EnsureSymbol(Document doc, BuiltInCategory category, TypeSpec s)
    {
        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(category)
            .Cast<FamilySymbol>().Where(x => x.FamilyName == s.Family).ToList();
        if (symbols.Count == 0)
            throw new ArgumentException($"family '{s.Family}' is not loaded in category {category}");

        var symbol = symbols.FirstOrDefault(x => x.Name == s.Name);
        var action = "unchanged";
        if (symbol == null)
        {
            var baseSymbol = s.BaseType == null
                ? symbols[0]
                : symbols.FirstOrDefault(x => x.Name == s.BaseType)
                  ?? throw new ArgumentException($"base_type '{s.BaseType}' not found in family '{s.Family}'");
            symbol = (FamilySymbol)baseSymbol.Duplicate(s.Name);
            action = "created";
        }

        foreach (var (paramName, mm) in s.ParamsMm)
        {
            var p = symbol.LookupParameter(paramName)
                    ?? throw new ArgumentException($"type parameter '{paramName}' not found on family '{s.Family}'");
            if (p.StorageType != StorageType.Double || p.IsReadOnly)
                throw new ArgumentException($"type parameter '{paramName}' on family '{s.Family}' is not an editable length");
            var target = MmUnits.ToFt(mm);
            if (Math.Abs(p.AsDouble() - target) > 1e-9)
            {
                p.Set(target);
                if (action == "unchanged") action = "updated";
            }
        }

        return new
        {
            kind = s.Kind, name = s.Name, family = s.Family, id = symbol.Id.Value, action,
            params_mm = s.ParamsMm.ToDictionary(kv => kv.Key, kv => MmUnits.FromFt(symbol.LookupParameter(kv.Key)!.AsDouble())),
        };
    }

    // ------------------------------------------------------------------ host types (floors, walls)

    private static object EnsureHostType<T>(Document doc, ModelLookup lookup, TypeSpec s) where T : HostObjAttributes
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
        var type = all.FirstOrDefault(t => t.Name == s.Name);
        var action = "unchanged";

        if (type == null)
        {
            var baseType = s.BaseType == null
                ? all.FirstOrDefault(t => t is not WallType w || w.Kind == WallKind.Basic)
                : all.FirstOrDefault(t => t.Name == s.BaseType);
            if (baseType == null)
                throw new ArgumentException($"base_type '{s.BaseType ?? "(any)"}' not found for {typeof(T).Name}");
            type = (T)baseType.Duplicate(s.Name);
            action = "created";
        }

        if (type is WallType wt && wt.Kind != WallKind.Basic)
            throw new ArgumentException($"wall type '{s.Name}' is {wt.Kind}; only Basic walls take layers");

        if (s.Layers.Count > 0)
        {
            var wanted = s.Layers.Select(l => new CompoundStructureLayer(
                MmUnits.ToFt(l.WidthMm), l.Function, lookup.GetMaterialId(l.Material))).ToList();
            var current = type.GetCompoundStructure();
            if (current == null || !SameLayers(current.GetLayers(), wanted))
            {
                var cs = CompoundStructure.CreateSimpleCompoundStructure(wanted);
                // Revit rejects a structure whose end-cap condition does not suit the element type
                // ("Input compound structure has wrong EndCap condition"): floors and roofs need NoEndCap,
                // walls keep their own. Take it from the type's current structure, which Revit already accepted.
                if (current != null) cs.EndCap = current.EndCap;
                else if (type is not WallType) cs.EndCap = EndCapCondition.NoEndCap;
                var structural = wanted.FindIndex(l => l.Function == MaterialFunctionAssignment.Structure);
                if (structural >= 0) cs.StructuralMaterialIndex = structural;
                type.SetCompoundStructure(cs);
                if (action == "unchanged") action = "updated";
            }
        }

        var layers = type.GetCompoundStructure()?.GetLayers()
            .Select(l => new { function = l.Function.ToString(), width_mm = MmUnits.FromFt(l.Width), material_id = l.MaterialId.Value })
            .ToList();
        var total = type.GetCompoundStructure()?.GetWidth() ?? 0;

        return new { kind = s.Kind, name = s.Name, id = type.Id.Value, action, total_mm = MmUnits.FromFt(total), layers };
    }

    private static bool SameLayers(IList<CompoundStructureLayer> a, IList<CompoundStructureLayer> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Function != b[i].Function || Math.Abs(a[i].Width - b[i].Width) > 1e-9 || a[i].MaterialId != b[i].MaterialId)
                return false;
        return true;
    }

    // ------------------------------------------------------------------ params

    private static List<TypeSpec> Parse(JsonElement p)
    {
        SpecReader.Obj(p);
        var specs = new List<TypeSpec>();
        foreach (var t in SpecReader.Items(p, "types", required: true))
        {
            var kind = SpecReader.Str(t, "kind");
            var name = SpecReader.Str(t, "name");
            var family = SpecReader.OptStr(t, "family");
            if ((kind == "structural_column" || kind == "column") && family == null)
                throw new ArgumentException($"type '{name}': 'family' is required for {kind}");

            var paramsMm = new Dictionary<string, double>(StringComparer.Ordinal);
            if (t.TryGetProperty("params_mm", out var pm) && pm.ValueKind == JsonValueKind.Object)
                foreach (var prop in pm.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Number || !double.IsFinite(prop.Value.GetDouble()))
                        throw new ArgumentException($"type '{name}': params_mm.{prop.Name} must be a number");
                    paramsMm[prop.Name] = prop.Value.GetDouble();
                }

            var layers = new List<LayerSpec>();
            foreach (var l in SpecReader.Items(t, "layers"))
            {
                var fn = SpecReader.Str(l, "function");
                if (!Enum.TryParse<MaterialFunctionAssignment>(fn, ignoreCase: true, out var function))
                    throw new ArgumentException($"type '{name}': unknown layer function '{fn}'");
                var width = SpecReader.Num(l, "width_mm");
                if (width < 0) throw new ArgumentException($"type '{name}': layer width must be >= 0");
                layers.Add(new LayerSpec(function, width, SpecReader.OptStr(l, "material")));
            }
            if ((kind == "floor" || kind == "wall") && layers.Count > 0 && layers.Sum(l => l.WidthMm) <= 0)
                throw new ArgumentException($"type '{name}': total layer width must be > 0");

            specs.Add(new TypeSpec(kind, name, family, SpecReader.OptStr(t, "base_type"), paramsMm, layers));
        }
        SpecReader.Unique(specs.Select(s => s.Kind + ":" + s.Name), "type");
        return specs;
    }
}
