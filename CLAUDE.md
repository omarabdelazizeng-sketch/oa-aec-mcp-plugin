# CLAUDE.md — OA AEC MCP Plugin

> **Read this file before suggesting any code change in this repository.**
> This is the C# Revit-side plugin for the AEC Workflows MCP server. It's a single-purpose
> WebSocket bridge — different from a typical Revit add-in. Different rules apply.

---

## What this project is

The Revit-side companion to `oa-aec-mcp` (the TypeScript MCP server). Together they expose curated AEC workflow tools to Claude Desktop, Claude Code, and other MCP clients.

**This plugin's only job:**
1. Start a WebSocket server inside the Revit process on add-in load
2. Accept JSON-RPC 2.0 requests over `ws://localhost:8765`
3. Dispatch each request via `ExternalEvent` so it runs on Revit's main thread
4. Marshal results back as JSON-RPC 2.0 responses

**What this plugin is NOT:**
- It is **not a Revit UI add-in.** No ribbon buttons, no tool windows, no MVVM, no WPF, no themes. The user interacts with this plugin entirely through Claude (or another MCP client), never through Revit's UI.
- It is **not multi-version yet.** v0.1 targets Revit 2025 only. Multi-version is a v0.2+ concern.
- It is **not OA-Tools.** OA-Tools is the separate, multi-version, UI-heavy Revit add-in. This plugin is intentionally minimal — every feature added here is one more thing that can break the bridge.

---

## Stack

| Layer | Technology |
|---|---|
| Language | C# 12 on .NET 8 |
| Revit version target | 2025 only (v0.1) |
| Revit API | `Nice3point.Revit.Api.*` NuGet packages |
| WebSocket server | `System.Net.WebSockets` (built into .NET, no external dependency) |
| JSON | `System.Text.Json` (built-in, faster than Newtonsoft, no version conflicts with Revit's bundled Newtonsoft) |
| Threading | `ExternalEvent` + `IExternalEventHandler` for all Revit API access |
| Logging | Serilog → `%AppData%\OA-AEC-MCP\logs\plugin-{date}.log` |
| Build | Nice3point Revit SDK |

---

## Architecture (canonical)

```
TypeScript MCP server (oa-aec-mcp)
  ↕ ws://localhost:8765 (WebSocket, JSON-RPC 2.0)
THIS PLUGIN
  ├─ WebSocketServer  ← receive thread (System.Net.WebSockets)
  ├─ CommandRegistry  ← maps method name → ICommand
  ├─ CommandDispatcher  ← uses ExternalEvent to push work to main thread
  └─ Commands/*.cs    ← actual Revit API work (ICommand implementations)
       ↕ Revit API
     Revit application (main thread)
```

**The threading boundary is at the CommandDispatcher, not inside individual commands.**

This is a deliberate divergence from `mcp-servers-for-revit`, which pushes the ExternalEvent boundary into each command. Centralizing it makes commands simpler (just write the Revit logic, no threading boilerplate) and makes the threading rule impossible to forget.

---

## The protocol (LOCKED — do not reinvent)

See full spec in the `oa-context` repo's SPRINT-NOTES.md. Summary:

**Request envelope:**
```json
{
  "jsonrpc": "2.0",
  "method": "<command_name>",
  "params": { ... },
  "id": "<uuid>"
}
```

**Success response:**
```json
{
  "jsonrpc": "2.0",
  "result": { ... },
  "id": "<uuid>"
}
```

**Error response:**
```json
{
  "jsonrpc": "2.0",
  "error": { "code": <int>, "message": "<str>", "data": { ... optional ... } },
  "id": "<uuid>"
}
```

**Standard JSON-RPC 2.0 error codes:**
- `-32700` Parse error (malformed JSON)
- `-32600` Invalid request (missing required fields)
- `-32601` Method not found
- `-32602` Invalid params
- `-32603` Internal error (unexpected exception)
- `-32000` Revit document not active
- `-32001` Revit timeout (ExternalEvent didn't complete within 30s)
- `-32002` Revit busy (another command in flight)

**Rules:**
- The `id` field must be echoed back verbatim. Never generate a new id on the response side.
- All errors return an error envelope, never a success envelope with an "error" key inside `result`.
- WebSocket close cleanly on add-in shutdown; do not leave dangling threads.

---

## Coding rules

### Threading — Revit API access ONLY through ExternalEvent

This is the single most important rule in this codebase. **Every Revit API call must run on Revit's main thread, via `ExternalEvent`.**

The WebSocket server runs on background threads. Commands receive their params there. But the moment a command needs to touch `Document`, `UIDocument`, `Element`, `Transaction`, or any other Revit API type, it must hand off to an ExternalEvent handler.

Pattern (in `CommandDispatcher`):

```csharp
public async Task<object> DispatchAsync(ICommand command, JsonElement parameters)
{
    var tcs = new TaskCompletionSource<object>();
    var handler = new CommandExternalEventHandler(command, parameters, tcs);
    var externalEvent = ExternalEvent.Create(handler);
    externalEvent.Raise();

    // Wait for the handler to complete, with a 30s timeout
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    cts.Token.Register(() => tcs.TrySetException(new TimeoutException("Revit timeout")));

    return await tcs.Task;
}
```

The command itself is a simple class:

```csharp
public interface ICommand
{
    string Name { get; }
    object Execute(Document doc, JsonElement parameters);  // runs on MAIN thread
}
```

Inside `Execute`, you have full Revit API access. Outside it (in WebSocket handlers, JSON parsing, etc.), you do **not.**

**Forbidden:**
- `Task.Run(() => doc.SomeApiCall())` — Revit API on a background thread will silently corrupt or fail.
- Storing `Document` or `UIDocument` references across requests — they may be invalidated.
- Holding a `Transaction` across an async boundary.

### Logging — Serilog, structured, never silent

Every WebSocket lifecycle event and every command dispatch must be logged with enough context to debug:

```csharp
Log.Information("Command {Method} dispatched, id={RequestId}", request.Method, request.Id);
Log.Error(ex, "Command {Method} failed, id={RequestId}", request.Method, request.Id);
```

Forbidden:
- `catch { }` or `catch (Exception) { }` — never swallow
- `try { ... } catch (Exception ex) { return null; }` — never silent return
- Log without context (no `Log.Error("failed")` without identifying which request)

Allowed only with explicit comment:

```csharp
catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
{
    // Client disconnected mid-request — expected, not an error
    Log.Debug("WebSocket client disconnected during request {RequestId}", currentRequestId);
}
```

### Error mapping — every exception becomes a JSON-RPC error

There is no such thing as a "let it propagate" exception in this codebase. Every command execution path eventually maps to either a success response or an error response. No exception escapes the dispatcher.

Mapping table:
- `KeyNotFoundException` on method lookup → `-32601` Method not found
- `JsonException` on params parsing → `-32700` Parse error
- `ArgumentException` from inside a command → `-32602` Invalid params
- `Autodesk.Revit.Exceptions.*` → typically `-32603` Internal error with the Revit exception type in `data`
- `TimeoutException` from ExternalEvent wait → `-32001` Revit timeout
- Any other → `-32603` Internal error with stack trace in `data` (only in debug builds; production strips stack traces)

### No global state for documents

```csharp
// FORBIDDEN
private static Document _currentDoc;

// REQUIRED
// Always get doc from UIApplication.ActiveUIDocument inside the ExternalEvent handler
var doc = uiApp.ActiveUIDocument?.Document;
if (doc == null) throw new InvalidOperationException("No active Revit document");
```

### Self-review — required after every change

Before reporting done on any code change, re-read the affected files end to end. Verify:

1. **All Revit API calls happen inside an `IExternalEventHandler.Execute` method.** Search for any `Document`, `Element`, `Transaction` reference; trace its origin; confirm it's inside the handler.
2. **Every catch block either logs and rethrows, or maps to a specific JSON-RPC error.** No silent swallows.
3. **WebSocket lifecycle is clean.** Connections close gracefully on add-in shutdown.
4. **No `Task.Run`, `Thread.Start`, or similar wrapping Revit API access.**
5. **JSON deserialization tolerates missing optional fields.** `params` may be absent entirely.

---

## Project structure (target — adapt as the project grows)

```
oa-aec-mcp-plugin/
├── src/
│   ├── OaAecMcpPlugin.csproj
│   ├── OaAecMcpPlugin.addin              ← Revit manifest
│   ├── PluginApplication.cs              ← IExternalApplication, lifecycle
│   ├── WebSocket/
│   │   ├── WebSocketServer.cs            ← accepts connections, parses JSON-RPC
│   │   └── JsonRpcMessages.cs            ← request/response/error types
│   ├── Dispatch/
│   │   ├── CommandDispatcher.cs          ← ExternalEvent bridge
│   │   ├── CommandRegistry.cs            ← method name → ICommand map
│   │   └── CommandExternalEventHandler.cs
│   ├── Commands/
│   │   ├── ICommand.cs
│   │   ├── PingCommand.cs                ← Day 2: round-trip ping
│   │   ├── SummarizeModelHealthCommand.cs   ← Day 3-4: T2
│   │   ├── ListUnplacedRoomsCommand.cs   ← T3
│   │   ├── FindWarningsByCategoryCommand.cs ← T5
│   │   └── AuditNamingConventionsCommand.cs ← T1
│   └── Services/
│       └── Logging.cs                    ← Serilog setup
├── README.md
├── CHANGELOG.md
├── CLAUDE.md                              ← this file
├── LICENSE                                ← MIT
└── .gitignore
```

Don't introduce a separate `Models/` or `Common/` folder until there's reusable shared code. Premature abstraction is more expensive than mild duplication.

---

## What to do FIRST (Day 2 of sprint)

Build only this much, in order:

1. `PluginApplication.cs` — empty `IExternalApplication` that logs "loaded" on `OnStartup`
2. `WebSocketServer.cs` — accepts WebSocket connections on `ws://localhost:8765`, logs every connect/disconnect
3. `JsonRpcMessages.cs` — simple C# records for request, success response, error response
4. `Commands/ICommand.cs` + `Commands/PingCommand.cs` — single command that returns `{ "result": "pong", "received_at": "<iso8601>" }`
5. `CommandRegistry.cs` + `CommandDispatcher.cs` — minimal version, dispatches `ping` through ExternalEvent (even though ping doesn't need Revit API yet, this proves the dispatch path)

**Day 2 success metric:** From Claude Desktop, send a `ping` request via the TypeScript MCP server, see "ping received" in Revit log, see "pong" returned in Claude Desktop chat.

Do NOT build any of the four real workflow tools (T1/T2/T3/T5) on Day 2. They come after the round-trip works.

---

## Anti-patterns to refuse

When asked to do any of these, push back with the alternative:

- "Just call the Revit API directly from the WebSocket handler" → no, dispatch via ExternalEvent
- "Use Newtonsoft.Json for parsing" → no, `System.Text.Json`; Newtonsoft conflicts with Revit's bundled version
- "Add a UI button to start/stop the server" → no, lifecycle is bound to add-in startup/shutdown
- "Cache the active document for performance" → no, always look it up fresh
- "Run the WebSocket on the main thread to avoid threading complexity" → no, that freezes Revit
- "Use port 8080 to match mcp-servers-for-revit" → no, 8765; 8080 collides with HTTP services
- "Skip ExternalEvent for read-only operations" → no, even read access must be on main thread to avoid race conditions with user actions
- "Add OAuth / API key authentication" → no, localhost only for v0.1
- "Add multi-version Revit support now" → no, v0.2+

---

## When you're not sure, ask

This plugin has hard rules around threading and protocol that fail silently when violated. If a request is ambiguous on those axes — especially threading — ask before generating code.
