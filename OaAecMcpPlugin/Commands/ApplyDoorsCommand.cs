using Autodesk.Revit.DB;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Creates/replaces wall-hosted doors by Mark in one transaction; identical specs leave instances alone.
///     Params: { "doors": [{ "mark": "D-01", "family": "Door", "type": "900 x 2100",
///       "host_wall_mark": "W-01", "position": [1000,0], "level": "Level 1",
///       "sill_height_mm": 0, "flip_hand": false, "flip_facing": false }] }.
///     Level defaults to the host wall's base level; sill defaults to zero. All lengths are millimetres.
///     Execute runs on Revit's main thread through CommandExternalEventHandler.
/// </summary>
public class ApplyDoorsCommand : ICommand
{
    /// <summary>The JSON-RPC command name.</summary>
    public string Name => "apply_doors";

    /// <summary>Validates and applies a batch on Revit's main thread.</summary>
    public object Execute(Document? doc, JsonElement parameters) =>
        ApplyOpenings.Execute(doc, parameters, "doors", BuiltInCategory.OST_Doors);
}
