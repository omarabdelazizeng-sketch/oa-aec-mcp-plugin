using Autodesk.Revit.UI;
using OaAecMcpPlugin.Commands;
using Serilog;
using System.Text.Json;

namespace OaAecMcpPlugin.Dispatch;

/// <summary>
///     Bridges the WebSocket receive thread to Revit's main thread via <see cref="ExternalEvent"/>.
///
///     Threading contract:
///     - <see cref="DispatchAsync"/> is called from background WebSocket threads.
///     - Actual command execution happens inside <see cref="CommandExternalEventHandler.Execute"/>
///       on Revit's main thread.
///     - The caller awaits a <see cref="TaskCompletionSource{T}"/> that the handler resolves.
///
///     <see cref="ExternalEvent.Create"/> is called in this constructor, which must therefore
///     be instantiated on Revit's main thread (i.e. from <c>Application.OnStartup</c>).
///     The threading boundary lives here in the Dispatch layer — Application.cs only handles
///     lifecycle (logger + server start/stop).
/// </summary>
public class CommandDispatcher
{
    private readonly CommandRegistry _registry;
    private readonly CommandExternalEventHandler _handler;
    private readonly ExternalEvent _externalEvent;

    /// <summary>
    ///     Must be constructed on Revit's main thread so that
    ///     <see cref="ExternalEvent.Create"/> is called in the correct context.
    /// </summary>
    public CommandDispatcher(CommandRegistry registry, CommandExternalEventHandler handler)
    {
        _registry = registry;
        _handler = handler;
        // ExternalEvent.Create requires the main thread — guaranteed because this
        // constructor is called from Application.StartWebSocketServer() → OnStartup().
        _externalEvent = ExternalEvent.Create(handler);
    }

    /// <summary>
    ///     Dispatch a JSON-RPC method to its registered command and await the result.
    ///     Times out after 30 seconds if Revit's main thread does not process the event.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Method not registered in the command registry.</exception>
    /// <exception cref="TimeoutException">Revit did not process the ExternalEvent within 30 s.</exception>
    /// <exception cref="Exception">Any exception thrown by the command's Execute method.</exception>
    public async Task<object> DispatchAsync(string method, JsonElement parameters)
    {
        if (!_registry.TryGet(method, out var command))
            throw new KeyNotFoundException($"Unknown method: {method}");

        // RunContinuationsAsynchronously: prevents the continuation from running synchronously
        // on Revit's main thread when TrySetResult is called, which would block it.
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        _handler.Enqueue(command!, parameters, tcs);
        _externalEvent.Raise();

        Log.Information("Command {Method} enqueued for main-thread dispatch", method);

        // Arm a 30-second timeout. The CancellationTokenSource registration fires
        // TrySetException — TrySet* is safe to call even after the TCS is already resolved.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() =>
            tcs.TrySetException(new TimeoutException($"Revit ExternalEvent timed out waiting for '{method}'")));

        return await tcs.Task;
    }
}
