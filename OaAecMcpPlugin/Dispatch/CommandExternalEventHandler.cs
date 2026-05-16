using Autodesk.Revit.UI;
using OaAecMcpPlugin.Commands;
using Serilog;
using System.Collections.Concurrent;
using System.Text.Json;

namespace OaAecMcpPlugin.Dispatch;

/// <summary>
///     Single shared <see cref="IExternalEventHandler"/> that drains a thread-safe queue of
///     pending commands on Revit's main thread.
///
///     One instance is created during <c>Application.OnStartup</c> (on the main thread) and
///     reused for every dispatched request — callers enqueue work from WebSocket threads,
///     then Revit fires <see cref="Execute"/> on the main thread to process the queue.
/// </summary>
public class CommandExternalEventHandler : IExternalEventHandler
{
    private readonly ConcurrentQueue<WorkItem> _queue = new();

    private readonly record struct WorkItem(
        ICommand Command,
        JsonElement Parameters,
        TaskCompletionSource<object> Tcs
    );

    /// <summary>
    ///     Enqueue a command for execution on Revit's main thread.
    ///     Safe to call from any thread.
    /// </summary>
    public void Enqueue(ICommand command, JsonElement parameters, TaskCompletionSource<object> tcs) =>
        _queue.Enqueue(new WorkItem(command, parameters, tcs));

    /// <summary>
    ///     Called by Revit on the main thread. Drains all pending work items.
    ///     Each item's TCS is resolved with either the command result or the thrown exception.
    /// </summary>
    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                // Get doc fresh — never cache across requests.
                var doc = app.ActiveUIDocument?.Document;
                var result = item.Command.Execute(doc, item.Parameters);
                item.Tcs.TrySetResult(result);
                Log.Debug("Command {Name} completed on main thread", item.Command.Name);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Command {Name} threw during Execute on main thread", item.Command.Name);
                item.Tcs.TrySetException(ex);
            }
        }
    }

    public string GetName() => "OA-AEC-MCP Command Handler";
}
