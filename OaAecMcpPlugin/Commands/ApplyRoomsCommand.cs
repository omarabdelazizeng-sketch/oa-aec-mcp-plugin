using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.Revit.Extensions;
using OaAecMcpPlugin.Services;
using Serilog;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Creates or updates placed rooms in ONE transaction, matched by number (unique in the project).
///     A room whose boundary already contains the requested point keeps its element and has name,
///     department and vertical limits updated; any other match is replaced. Rooms are never created
///     inside an existing room of another number (redundant), and rooms that Revit cannot enclose are
///     refused (batch rolled back) unless allow_unenclosed is true, in which case they are returned with placed=false.
///     base_offset_mm must not exceed the level's Computation Height (Revit rejects it); the tool reports this instead of changing the level.
///     Rooms in links and rooms whose number is not in the spec are never touched.
///
///     Params: { "rooms": [{ "number": "APT 4-02", "name": "RECEPTION+DINING", "level": "First Floor Level",
///       "x": -6618, "y": 7247, "base_offset_mm": 0, "upper_limit_level": "First Floor Level",
///       "limit_offset_mm": 2800, "department": "APT 4" }] }
///     base_offset_mm is measured up from the room's level, limit_offset_mm from the upper limit level
///     (default: the room's own level), so the room runs from the finished floor to the underside of the ceiling.
///     x/y are plan coordinates in mm of a point inside the room.
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on Revit's main thread.
/// </summary>
public class ApplyRoomsCommand : ICommand
{
    /// <summary>The JSON-RPC command name.</summary>
    public string Name => "apply_rooms";

    private const double Tol = 1e-9;
    private const double SqFtToM2 = 0.09290304;
    private const double MinRoomHeightMm = 200;

    private sealed record RoomSpec(string Number, string Name, string Level, double X, double Y, double BaseOffsetMm,
        string? UpperLevel, double LimitOffsetMm, string? Department);

    /// <summary>Validates and applies the batch on Revit's main thread.</summary>
    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null) throw new InvalidOperationException("No active Revit document");
        if (doc.IsFamilyDocument) throw new ArgumentException("apply_rooms needs a project document");

        var specs = Parse(parameters);
        var allowUnenclosed = SpecReader.Bool(parameters, "allow_unenclosed", false);
        var lookup = new ModelLookup(doc);
        var resolved = specs.Select(s =>
        {
            var level = Context(s.Number, "level", () => lookup.GetLevel(s.Level));
            var upper = Context(s.Number, "upper_limit_level", () => s.UpperLevel == null ? level : lookup.GetLevel(s.UpperLevel));
            var heightFt = upper.ProjectElevation + MmUnits.ToFt(s.LimitOffsetMm) - level.ProjectElevation - MmUnits.ToFt(s.BaseOffsetMm);
            if (MmUnits.FromFt(heightFt) < MinRoomHeightMm)
                throw new ArgumentException($"room '{s.Number}': 'limit_offset_mm', 'upper_limit_level' and 'base_offset_mm' give a room height below {MinRoomHeightMm} mm");
            var computation = level.get_Parameter(BuiltInParameter.LEVEL_ROOM_COMPUTATION_HEIGHT);
            if (computation != null && s.BaseOffsetMm > MmUnits.FromFt(computation.AsDouble()) + 1e-6)
                throw new ArgumentException($"room '{s.Number}': 'base_offset_mm' {s.BaseOffsetMm} is above the Computation Height of level '{s.Level}' ({Math.Round(MmUnits.FromFt(computation.AsDouble()), 1)} mm); Revit rejects it. Use a lower offset or raise the level's Computation Height first");
            return (Spec: s, Level: level, Upper: upper, HeightFt: heightFt);
        }).ToList();

        var results = new List<object>();
        var untouched = 0;

        var warnings = RevitTx.Run(doc, "OA-AEC-MCP: apply_rooms", () =>
        {
            var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().Cast<Room>().ToList();
            var wantedNumbers = resolved.Select(r => r.Spec.Number).ToHashSet(StringComparer.Ordinal);
            var levelOf = resolved.ToDictionary(r => r.Spec.Number, r => r.Level.Id, StringComparer.Ordinal);
            var byKey = new Dictionary<string, Room>(StringComparer.Ordinal);
            foreach (var room in all)
            {
                var key = room.Number;
                if (!wantedNumbers.Contains(key)) { untouched++; continue; }
                if (!byKey.TryAdd(key, room))
                    throw new ArgumentException($"room '{room.Number}': duplicate number in the model; fix duplicates first");
                if (room.LevelId != levelOf[key])
                    throw new ArgumentException($"room '{room.Number}': the number is already used by a room on level '{doc.GetElement(room.LevelId)?.Name}'; room numbers must be unique in the project");
            }

            var applied = new List<(RoomSpec Spec, Room Room, XYZ Point)>();
            var actions = new List<string>();
            var changed = false;
            foreach (var r in resolved)
            {
                var s = r.Spec;
                var point = new XYZ(MmUnits.ToFt(s.X), MmUnits.ToFt(s.Y), r.Level.ProjectElevation + MmUnits.ToFt(s.BaseOffsetMm) + r.HeightFt / 2);
                string action;
                Room room;
                if (byKey.TryGetValue(s.Number, out var existing))
                {
                    if (existing.Area > 0 && existing.IsPointInRoom(point))
                    {
                        room = existing;
                        action = Update(room, s, r.Upper) ? "updated" : "unchanged";
                        if (action == "updated") changed = true;
                        applied.Add((s, room, point));
                        actions.Add(action);
                        continue;
                    }
                    doc.Delete(existing.Id);
                    all.Remove(existing);
                    action = "replaced";
                }
                else action = "created";

                foreach (var other in all)
                {
                    if (!other.IsValidObject || other.Area <= 0 || other.LevelId != r.Level.Id) continue;
                    if (other.IsPointInRoom(point))
                        throw new ArgumentException($"room '{s.Number}': the point lies inside existing room '{other.Number}' ({other.Name}); a new room there would be redundant");
                }

                room = doc.Create.NewRoom(r.Level, new UV(MmUnits.ToFt(s.X), MmUnits.ToFt(s.Y)));
                room.Number = s.Number;
                Update(room, s, r.Upper);
                doc.Regenerate();
                all.Add(room);
                applied.Add((s, room, point));
                actions.Add(action);
                changed = true;
            }

            if (changed) doc.Regenerate();

            var unenclosed = applied.Where(a => !a.Room.IsValidObject || a.Room.Area <= 0).Select(a => a.Spec.Number).ToList();
            if (unenclosed.Count > 0 && !allowUnenclosed)
                throw new InvalidOperationException($"rooms not enclosed by walls (batch rolled back): {string.Join(", ", unenclosed)}. Close the walls (or add room separation lines) or pass allow_unenclosed=true");

            // Two applied rooms must not share a region; a spec point must lie in its own room when it is placed.
            foreach (var a in applied)
            {
                if (!a.Room.IsValidObject || a.Room.Area <= 0) continue;
                if (!a.Room.IsPointInRoom(a.Point))
                    throw new InvalidOperationException($"room '{a.Spec.Number}': the point is not inside the resulting room; batch rolled back");
                foreach (var b in applied)
                    if (!ReferenceEquals(a.Room, b.Room) && b.Room.IsValidObject && b.Room.Area > 0 && b.Room.LevelId == a.Room.LevelId
                        && b.Room.IsPointInRoom(a.Point))
                        throw new InvalidOperationException($"rooms '{a.Spec.Number}' and '{b.Spec.Number}' share one region (redundant); batch rolled back");
            }

            for (var i = 0; i < applied.Count; i++)
                results.Add(Result(applied[i].Spec, applied[i].Room, actions[i]));
        });

        return new
        {
            document = doc.Title, transaction = "committed", rooms = results,
            other_rooms_untouched = untouched, revit_warnings = warnings,
        };
    }

    private static bool Update(Room room, RoomSpec s, Level upper)
    {
        var changed = false;
        var nameParam = room.get_Parameter(BuiltInParameter.ROOM_NAME)
                        ?? throw new ArgumentException($"room '{s.Number}': name is not editable");
        if (nameParam.AsString() != s.Name) { nameParam.Set(s.Name); changed = true; }
        if (s.Department != null && room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT) is { IsReadOnly: false } d && d.AsString() != s.Department)
        {
            d.Set(s.Department);
            changed = true;
        }
        changed |= SetLength(room, BuiltInParameter.ROOM_LOWER_OFFSET, s.BaseOffsetMm);
        changed |= SetLength(room, BuiltInParameter.ROOM_UPPER_OFFSET, s.LimitOffsetMm);
        var up = room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL)
                 ?? throw new ArgumentException($"room '{s.Number}': upper limit is not editable");
        if (up.AsElementId() != upper.Id) { up.Set(upper.Id); changed = true; }
        return changed;
    }

    private static bool SetLength(Room room, BuiltInParameter bip, double mm)
    {
        var p = room.get_Parameter(bip) ?? throw new ArgumentException($"room '{room.Number}': '{bip}' is missing");
        if (Math.Abs(p.AsDouble() - MmUnits.ToFt(mm)) <= Tol) return false;
        p.Set(MmUnits.ToFt(mm));
        return true;
    }

    private static object Result(RoomSpec s, Room room, string action)
    {
        var area = room.IsValidObject ? room.Area * SqFtToM2 : 0;
        return new
        {
            number = s.Number, id = room.Id.ToLong(), action, name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "", level = s.Level,
            area_m2 = Math.Round(area, 3), placed = area > 0,
            base_offset_mm = s.BaseOffsetMm, limit_offset_mm = s.LimitOffsetMm,
            upper_limit = s.UpperLevel ?? s.Level, department = s.Department,
        };
    }

    private static List<RoomSpec> Parse(JsonElement p)
    {
        SpecReader.Obj(p);
        var specs = new List<RoomSpec>();
        foreach (var r in SpecReader.Items(p, "rooms", required: true))
        {
            var number = Context($"at rooms[{specs.Count}] (number unavailable)", "number",
                () => SpecReader.Str(SpecReader.Obj(r, "room"), "number"));
            specs.Add(Context(number, "spec", () =>
            {
                var limit = SpecReader.Num(r, "limit_offset_mm");
                if (limit <= 0 || limit > 30000) throw new ArgumentException("'limit_offset_mm' must be positive and at most 30000");
                return new RoomSpec(number, SpecReader.Str(r, "name"), SpecReader.Str(r, "level"), SpecReader.Num(r, "x"),
                    SpecReader.Num(r, "y"), SpecReader.Num(r, "base_offset_mm", 0), SpecReader.OptStr(r, "upper_limit_level"),
                    limit, SpecReader.OptStr(r, "department"));
            }));
        }
        if (specs.Count == 0) throw new ArgumentException("'rooms' is empty");
        SpecReader.Unique(specs.Select(s => s.Number), "room number (numbers must be unique in the project)");
        return specs;
    }

    private static T Context<T>(string number, string field, Func<T> read)
    {
        try { return read(); }
        catch (ArgumentException ex)
        {
            Log.Warning(ex, "Invalid room {Number}, field {Field}", number, field);
            throw new ArgumentException($"room '{number}': '{field}': {ex.Message}", ex);
        }
    }
}
