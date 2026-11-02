using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Returns a health snapshot of the active Revit model:
///     warning count, unplaced rooms, unused families, view count,
///     total element count, document title, and a plain-English summary.
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on
///     Revit's main thread — all Revit API calls here are therefore safe.
/// </summary>
public class SummarizeModelHealthCommand : ICommand
{
    public string Name => "summarize_model_health";

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null)
            throw new InvalidOperationException("No active Revit document");

        // 1. Warning count
        var warningCount = doc.GetWarnings().Count;

        // 2. Unplaced rooms — Area < 0.001 is the correct signal: Location can be non-null
        //    on rooms with zero area (e.g. rooms placed but not bounded), so checking Area
        //    is more accurate than checking Location.
        var unplacedRoomCount = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Count(r => r.Area < 0.001);

        // 3. Unused families: not in-place AND zero instances in the model.
        //    Instance check uses family.FamilyCategory as the collector category filter.
        var unusedFamilyCount = new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .Count(family =>
            {
                if (family.IsInPlace) return false;
                var cat = family.FamilyCategory;
                if (cat == null) return false;
                var instanceCount = new FilteredElementCollector(doc)
                    .OfCategoryId(cat.Id)
                    .WhereElementIsNotElementType()
                    .GetElementCount();
                return instanceCount == 0;
            });

        // 4. View count, excluding view templates.
        var viewCount = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Views)
            .Cast<View>()
            .Count(v => !v.IsTemplate);

        // 5. Total placed element count.
        var totalElementCount = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .GetElementCount();

        // 6. Document title.
        var title = doc.Title;

        // Build plain-English summary — mention each issue only when above the threshold.
        var issues = new List<string>();
        if (warningCount > 0)
            issues.Add($"{warningCount} warning{(warningCount == 1 ? "" : "s")} require attention");
        if (unplacedRoomCount > 0)
            issues.Add($"{unplacedRoomCount} room{(unplacedRoomCount == 1 ? "" : "s")} {(unplacedRoomCount == 1 ? "is" : "are")} unplaced");
        if (unusedFamilyCount > 10)
            issues.Add($"{unusedFamilyCount} unused families are bloating the file");
        if (viewCount > 50)
            issues.Add($"view count is high ({viewCount}), which may slow the model");

        var summary = issues.Count == 0
            ? "Model looks healthy — no significant issues detected."
            : string.Join("; ", issues) + ".";

        return new
        {
            title,
            warningCount,
            unplacedRoomCount,
            unusedFamilyCount,
            viewCount,
            totalElementCount,
            summary
        };
    }
}
