using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Audits element names in the active Revit model against a caller-supplied regex pattern,
///     returning grouped violation reports with per-category truncation support.
///
///     Parameters:
///     - pattern (string, required): regex to test each element name against; names that do
///       NOT match are violations.
///     - categories (string[], optional): categories to audit; defaults to Views, Sheets, Rooms,
///       Levels. Unknown category names are returned in unknown_categories and skipped.
///     - max_violations (int, optional): cap on violations returned per category (default 50,
///       max 200). violation_count always reflects the true total; truncated signals capping.
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on
///     Revit's main thread — all Revit API calls here are therefore safe.
/// </summary>
public class AuditNamingConventionsCommand : ICommand
{
    public string Name => "audit_naming_conventions";

    private static readonly HashSet<string> KnownCategories = new(StringComparer.OrdinalIgnoreCase)
        { "Views", "Sheets", "Rooms", "Levels", "Walls", "Doors", "Windows", "Families" };

    private static readonly string[] DefaultCategories = ["Views", "Sheets", "Rooms", "Levels"];

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null)
            throw new InvalidOperationException("No active Revit document");

        // --- pattern (required) ---
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("pattern", out var patternProp) ||
            patternProp.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Required parameter 'pattern' is missing or not a string");

        var patternStr = patternProp.GetString()!;

        Regex regex;
        try
        {
            regex = new Regex(patternStr, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid regex pattern: {ex.Message}");
        }

        // --- categories (optional) ---
        string[] requestedCategories;
        if (parameters.TryGetProperty("categories", out var categoriesProp) &&
            categoriesProp.ValueKind == JsonValueKind.Array)
        {
            requestedCategories = categoriesProp.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray();
        }
        else
        {
            requestedCategories = DefaultCategories;
        }

        // --- max_violations (optional, default 50, clamped to [1, 200]) ---
        var maxViolations = 50;
        if (parameters.TryGetProperty("max_violations", out var maxProp) &&
            maxProp.ValueKind == JsonValueKind.Number &&
            maxProp.TryGetInt32(out var maxVal))
            maxViolations = Math.Clamp(maxVal, 1, 200);

        // Split known vs unknown categories, preserving original casing for known names.
        var knownToCheck = requestedCategories
            .Where(c => KnownCategories.Contains(c))
            .ToList();
        var unknownCategories = requestedCategories
            .Where(c => !KnownCategories.Contains(c))
            .ToList();

        var results = new List<object>();
        var totalElementsChecked = 0;
        var totalViolations = 0;

        foreach (var category in knownToCheck)
        {
            var (names, ids) = CollectNamesForCategory(doc, category);
            totalElementsChecked += names.Count;

            var violationCount = 0;
            var violations = new List<object>();

            for (var i = 0; i < names.Count; i++)
            {
                if (regex.IsMatch(names[i])) continue;
                violationCount++;
                if (violations.Count < maxViolations)
                    violations.Add(new { id = ids[i], name = names[i] });
            }

            totalViolations += violationCount;
            results.Add(new
            {
                category,
                total_checked   = names.Count,
                violation_count = violationCount,
                truncated       = violationCount > maxViolations,
                violations
            });
        }

        return new
        {
            pattern                = patternStr,
            categories_checked     = knownToCheck,
            unknown_categories     = unknownCategories,
            total_elements_checked = totalElementsChecked,
            total_violations       = totalViolations,
            results
        };
    }

    private static (List<string> names, List<long> ids) CollectNamesForCategory(
        Document doc, string category)
    {
        var names = new List<string>();
        var ids   = new List<long>();

        switch (category)
        {
            case "Views":
                foreach (var v in new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Views)
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.CanBePrinted))
                {
                    names.Add(v.Name);
                    ids.Add(v.Id.Value);
                }
                break;

            case "Sheets":
                foreach (var s in new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>())
                {
                    names.Add($"{s.SheetNumber} - {s.Name}");
                    ids.Add(s.Id.Value);
                }
                break;

            case "Rooms":
                foreach (var r in new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.Area > 0))
                {
                    names.Add(r.Name);
                    ids.Add(r.Id.Value);
                }
                break;

            case "Levels":
                foreach (var l in new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>())
                {
                    names.Add(l.Name);
                    ids.Add(l.Id.Value);
                }
                break;

            case "Walls":
                foreach (var wt in new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>())
                {
                    names.Add(wt.Name);
                    ids.Add(wt.Id.Value);
                }
                break;

            case "Doors":
                foreach (var fs in new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>())
                {
                    names.Add(fs.Name);
                    ids.Add(fs.Id.Value);
                }
                break;

            case "Windows":
                foreach (var fs in new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>())
                {
                    names.Add(fs.Name);
                    ids.Add(fs.Id.Value);
                }
                break;

            case "Families":
                foreach (var f in new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>())
                {
                    names.Add(f.Name);
                    ids.Add(f.Id.Value);
                }
                break;
        }

        return (names, ids);
    }
}
