using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Returns all unplaced rooms in the active Revit model — rooms whose area is
///     effectively zero because they are not enclosed by room-bounding elements.
///     Using Area &lt; 0.001 rather than Location == null because Location can be
///     non-null on rooms with zero area (e.g. rooms placed but not bounded).
///
///     Parameters (all optional):
///     - level (string): if present, only rooms on the matching level are returned
///       (case-insensitive exact match on level name).
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on
///     Revit's main thread — all Revit API calls here are therefore safe.
/// </summary>
public class ListUnplacedRoomsCommand : ICommand
{
    public string Name => "list_unplaced_rooms";

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null)
            throw new InvalidOperationException("No active Revit document");

        // Optional level filter — null means return all levels.
        string? levelFilter = null;
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("level", out var levelProp) &&
            levelProp.ValueKind == JsonValueKind.String)
            levelFilter = levelProp.GetString();

        var rooms = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(r => r.Area < 0.001)
            .Where(r => levelFilter == null ||
                        string.Equals(r.Level?.Name, levelFilter, StringComparison.OrdinalIgnoreCase))
            .Select(r => new
            {
                id         = r.Id.Value,
                name       = r.Name,
                department = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "",
                level_name = r.Level?.Name ?? "(no level)"
            })
            .OrderBy(r => r.level_name)
            .ThenBy(r => r.name)
            .ToList();

        return new
        {
            count = rooms.Count,
            rooms
        };
    }
}
