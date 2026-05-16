using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Returns the full list of unplaced rooms in the active Revit model —
///     rooms whose area is effectively zero because they are not enclosed by
///     room-bounding elements. Using Area &lt; 0.001 rather than Location == null
///     because Location can be non-null on rooms with zero area (e.g. rooms
///     that were placed but lost their bounding walls).
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

        var rooms = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(r => r.Area < 0.001)
            .Select(r => new
            {
                id = r.Id.Value,
                name = r.Name,
                number = r.Number,
                level = r.Level?.Name ?? "(no level)"
            })
            .OrderBy(r => r.level)
            .ThenBy(r => r.number)
            .ToList();

        return new
        {
            count = rooms.Count,
            rooms
        };
    }
}
