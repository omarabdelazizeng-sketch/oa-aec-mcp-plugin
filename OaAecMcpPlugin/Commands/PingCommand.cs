using Autodesk.Revit.DB;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Round-trip ping command. Does not use the Revit API, but is still dispatched
///     through ExternalEvent to validate the full threading path end-to-end.
/// </summary>
public class PingCommand : ICommand
{
    public string Name => "ping";

    public object Execute(Document? doc, JsonElement parameters) => new
    {
        result = "pong",
        received_at = DateTime.UtcNow.ToString("O")
    };
}
