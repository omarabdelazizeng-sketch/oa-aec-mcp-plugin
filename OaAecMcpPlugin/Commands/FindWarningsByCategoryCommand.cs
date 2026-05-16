using Autodesk.Revit.DB;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Returns Revit model warnings grouped by description text, with an optional
///     filter on the Revit category of the failing elements.
///
///     Parameters (all optional):
///     - category (string): if present and not "all", only warning groups where at
///       least one failing element belongs to the named category are returned
///       (case-insensitive exact match on category name, e.g. "Walls", "Rooms").
///
///     Returns: warning groups ordered by count descending, each with:
///     - description   (string) — the warning text from GetDescriptionText()
///     - count         (int)    — number of warnings with this description
///     - element_ids   (long[]) — up to 10 distinct failing element IDs across the group
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on
///     Revit's main thread — all Revit API calls here are therefore safe.
/// </summary>
public class FindWarningsByCategoryCommand : ICommand
{
    public string Name => "find_warnings_by_category";

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null)
            throw new InvalidOperationException("No active Revit document");

        // Optional category filter — null or "all" means return all categories.
        string? categoryFilter = null;
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("category", out var categoryProp) &&
            categoryProp.ValueKind == JsonValueKind.String)
        {
            var raw = categoryProp.GetString();
            if (!string.IsNullOrEmpty(raw) &&
                !string.Equals(raw, "all", StringComparison.OrdinalIgnoreCase))
                categoryFilter = raw;
        }

        var allWarnings = doc.GetWarnings();

        // Apply category filter: keep warnings where at least one failing element
        // belongs to the requested Revit category.
        IEnumerable<FailureMessage> warnings = allWarnings;
        if (categoryFilter != null)
        {
            warnings = allWarnings.Where(w =>
                w.GetFailingElements().Any(eid =>
                {
                    var el = doc.GetElement(eid);
                    return el?.Category != null &&
                           string.Equals(el.Category.Name, categoryFilter,
                               StringComparison.OrdinalIgnoreCase);
                }));
        }

        // Group by description text, order by count descending.
        var groups = warnings
            .GroupBy(w => w.GetDescriptionText())
            .Select(g => new
            {
                description = g.Key,
                count       = g.Count(),
                // Collect up to 10 distinct failing element IDs across all warnings in the group.
                element_ids = g.SelectMany(w => w.GetFailingElements())
                               .Select(eid => eid.Value)
                               .Distinct()
                               .Take(10)
                               .ToList()
            })
            .OrderByDescending(g => g.count)
            .ToList();

        return new
        {
            total_warnings = allWarnings.Count,
            group_count    = groups.Count,
            groups
        };
    }
}
