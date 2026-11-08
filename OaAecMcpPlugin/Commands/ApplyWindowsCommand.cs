using Autodesk.Revit.DB;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Creates/replaces wall-hosted windows by Mark in one transaction; identical specs leave instances alone.
///     Params: { "windows": [{ "mark": "WIN-01", "family": "Window", "type": "1200 x 1200",
///       "host_wall_mark": "W-01", "position": [1000,0], "level": "Level 1",
///       "sill_height_mm": 900, "flip_hand": false, "flip_facing": false }] }.
///     Level defaults to the host wall's base level. Omitted sill is left to Revit and is not constrained
///     during comparison. All lengths are millimetres. Execute runs on the ExternalEvent main thread.
/// </summary>
public class ApplyWindowsCommand : ICommand
{
    /// <summary>The JSON-RPC command name.</summary>
    public string Name => "apply_windows";

    /// <summary>Validates and applies a batch on Revit's main thread.</summary>
    public object Execute(Document? doc, JsonElement parameters) =>
        ApplyOpenings.Execute(doc, parameters, "windows", BuiltInCategory.OST_Windows);
}
