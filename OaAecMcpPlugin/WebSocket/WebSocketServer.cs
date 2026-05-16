using OaAecMcpPlugin.Dispatch;
using Serilog;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OaAecMcpPlugin.WebSocket;

/// <summary>
///     Listens on <c>ws://localhost:8765</c>, accepts WebSocket connections one at a time
///     (v0.1 — concurrent connections are a v0.2 concern), deserializes JSON-RPC 2.0 requests,
///     dispatches them through <see cref="CommandDispatcher"/>, and writes JSON-RPC responses back.
///
///     Lifecycle: <see cref="Start"/> is called from <c>Application.OnStartup</c>;
///     <see cref="Stop"/> is called from <c>Application.OnShutdown</c>.
///     All networking runs on background threads — never on Revit's main thread.
/// </summary>
public class WebSocketServer
{
    private readonly CommandDispatcher _dispatcher;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public WebSocketServer(CommandDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Start the HTTP listener and begin accepting WebSocket upgrade requests.</summary>
    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://localhost:8765/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));

        Log.Information("WebSocket server started on ws://localhost:8765");
    }

    /// <summary>Stop the server and wait up to 5 seconds for the accept loop to exit.</summary>
    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _acceptTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
        {
            // Expected on clean shutdown — accept loop cancelled.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during WebSocket server shutdown");
        }
        finally
        {
            Log.Information("WebSocket server stopped");
        }
    }

    // -------------------------------------------------------------------------
    // Accept loop
    // -------------------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException ex) when (ct.IsCancellationRequested)
            {
                // Listener stopped because Stop() was called — expected.
                Log.Debug("HttpListener closed during shutdown: {Message}", ex.Message);
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error in WebSocket accept loop");
                break;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                Log.Warning("Rejected non-WebSocket request from {RemoteEndPoint}", context.Request.RemoteEndPoint);
                continue;
            }

            // v0.1: handle one connection at a time (await before accepting the next).
            await HandleConnectionAsync(context, ct);
        }
    }

    // -------------------------------------------------------------------------
    // Connection handler
    // -------------------------------------------------------------------------

    private async Task HandleConnectionAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerWebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to upgrade HTTP request to WebSocket");
            return;
        }

        var ws = wsContext.WebSocket;
        Log.Information("WebSocket client connected from {RemoteEndPoint}", context.Request.RemoteEndPoint);

        try
        {
            await ReceiveLoopAsync(ws, ct);
        }
        finally
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Debug("Exception while closing WebSocket: {Message}", ex.Message);
                }
            }

            ws.Dispose();
            Log.Information("WebSocket client disconnected from {RemoteEndPoint}", context.Request.RemoteEndPoint);
        }
    }

    // -------------------------------------------------------------------------
    // Receive loop
    // -------------------------------------------------------------------------

    private async Task ReceiveLoopAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            // Accumulate frames into a MemoryStream to handle fragmented messages.
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult result;

            try
            {
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log.Debug("WebSocket close frame received");
                        return;
                    }
                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                // Client disconnected mid-message — expected, not an error.
                Log.Debug("WebSocket client disconnected prematurely during receive");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                Log.Warning("Received non-text WebSocket message (type={Type}), ignoring", result.MessageType);
                continue;
            }

            var json = Encoding.UTF8.GetString(messageStream.ToArray());
            var responseJson = await ProcessMessageAsync(json);

            try
            {
                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                await ws.SendAsync(
                    new ArraySegment<byte>(responseBytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    ct);
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                // Client disconnected before we could send the response.
                Log.Debug("WebSocket client disconnected before response could be sent");
                return;
            }
        }
    }

    // -------------------------------------------------------------------------
    // JSON-RPC message processing
    // -------------------------------------------------------------------------

    private async Task<string> ProcessMessageAsync(string json)
    {
        JsonRpcRequest? request = null;

        // Step 1: parse JSON — any parse failure is -32700.
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            Log.Warning("JSON parse error: {Message}", ex.Message);
            return ErrorResponse(null, -32700, "Parse error", ex.Message);
        }

        // Step 2: validate the envelope — missing/wrong fields are -32600.
        if (request is null || request.JsonRpc != "2.0" || string.IsNullOrWhiteSpace(request.Method))
        {
            Log.Warning("Invalid JSON-RPC request (missing jsonrpc/method), id={Id}", request?.Id);
            return ErrorResponse(request?.Id, -32600, "Invalid request",
                "jsonrpc must be \"2.0\" and method must be non-empty");
        }

        Log.Information("Command {Method} received, id={RequestId}", request.Method, request.Id);

        // Step 3: dispatch — map exceptions to JSON-RPC error codes per CLAUDE.md.
        object result;
        try
        {
            result = await _dispatcher.DispatchAsync(
                request.Method,
                request.Params ?? default);
        }
        catch (KeyNotFoundException ex)
        {
            Log.Warning("Method not found: {Method}, id={RequestId}", request.Method, request.Id);
            return ErrorResponse(request.Id, -32601, "Method not found", ex.Message);
        }
        catch (ArgumentException ex)
        {
            Log.Warning(ex, "Invalid params for {Method}, id={RequestId}", request.Method, request.Id);
            return ErrorResponse(request.Id, -32602, "Invalid params", ex.Message);
        }
        catch (TimeoutException ex)
        {
            Log.Error(ex, "Revit ExternalEvent timed out for {Method}, id={RequestId}", request.Method, request.Id);
            return ErrorResponse(request.Id, -32001, "Revit timeout", ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No active Revit document"))
        {
            Log.Warning("No active Revit document for {Method}, id={RequestId}", request.Method, request.Id);
            return ErrorResponse(request.Id, -32000, "No active Revit document", null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Internal error executing {Method}, id={RequestId}", request.Method, request.Id);
#if DEBUG
            return ErrorResponse(request.Id, -32603, "Internal error",
                new { type = ex.GetType().Name, message = ex.Message, stackTrace = ex.StackTrace });
#else
            return ErrorResponse(request.Id, -32603, "Internal error",
                new { type = ex.GetType().Name, message = ex.Message });
#endif
        }

        Log.Information("Command {Method} succeeded, id={RequestId}", request.Method, request.Id);
        return SuccessResponse(request.Id, result);
    }

    // -------------------------------------------------------------------------
    // Serialization helpers
    // -------------------------------------------------------------------------

    private static string SuccessResponse(string? id, object result)
    {
        var response = new JsonRpcSuccessResponse(result, id);
        return JsonSerializer.Serialize(response, SerializerOptions);
    }

    private static string ErrorResponse(string? id, int code, string message, object? data)
    {
        var response = new JsonRpcErrorResponse(new JsonRpcError(code, message, data), id);
        return JsonSerializer.Serialize(response, SerializerOptions);
    }
}
