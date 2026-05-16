using Autodesk.Revit.DB;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Contract for all JSON-RPC commands. Execute runs on Revit's main thread
///     inside an IExternalEventHandler — never call this directly from the WebSocket thread.
/// </summary>
public interface ICommand
{
    /// <summary>The JSON-RPC method name this command handles.</summary>
    string Name { get; }

    /// <summary>
    ///     Execute the command. Always called on Revit's main thread via ExternalEvent.
    ///     <paramref name="doc"/> is null when no project is open — commands that require
    ///     an active document should check and throw <see cref="InvalidOperationException"/>.
    /// </summary>
    object Execute(Document? doc, JsonElement parameters);
}
