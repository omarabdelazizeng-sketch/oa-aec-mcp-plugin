using Nice3point.Revit.Toolkit.External;
using OaAecMcpPlugin.Commands;
using OaAecMcpPlugin.Dispatch;
using Serilog;
using Serilog.Events;

namespace OaAecMcpPlugin;

/// <summary>
///     Revit add-in entry point. Owns the WebSocket server lifecycle.
///     No ribbon, no UI — this plugin is driven entirely by the MCP server over WebSocket.
/// </summary>
[UsedImplicitly]
public class Application : ExternalApplication
{
    private WebSocket.WebSocketServer? _server;

    public override void OnStartup()
    {
        CreateLogger();
        StartWebSocketServer();
    }

    public override void OnShutdown()
    {
        StopWebSocketServer();
        Log.CloseAndFlush();
    }

    // -------------------------------------------------------------------------
    // WebSocket lifecycle
    // -------------------------------------------------------------------------

    private void StartWebSocketServer()
    {
        try
        {
            var handler = new CommandExternalEventHandler();
            var registry = new CommandRegistry();
            registry.Register(new PingCommand());
            registry.Register(new SummarizeModelHealthCommand());
            registry.Register(new ListUnplacedRoomsCommand());
            registry.Register(new FindWarningsByCategoryCommand());
            registry.Register(new AuditNamingConventionsCommand());

            // CommandDispatcher creates the ExternalEvent internally (requires main thread).
            // OnStartup IS on the main thread, so this is correct.
            var dispatcher = new CommandDispatcher(registry, handler);

            _server = new WebSocket.WebSocketServer(dispatcher);
            _server.Start();

            Log.Information("OA-AEC-MCP plugin loaded — WebSocket server running on ws://localhost:8765");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to start WebSocket server — plugin will not function");
        }
    }

    private void StopWebSocketServer()
    {
        try
        {
            _server?.Stop();
            Log.Information("OA-AEC-MCP plugin unloaded");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping WebSocket server during shutdown");
        }
    }

    // -------------------------------------------------------------------------
    // Logging setup
    // -------------------------------------------------------------------------

    private static void CreateLogger()
    {
        const string outputTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OA-AEC-MCP", "logs", "plugin-.log");

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Debug(LogEventLevel.Debug, outputTemplate)
            .WriteTo.File(
                logPath,
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: outputTemplate)
            .MinimumLevel.Debug()
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = (Exception)args.ExceptionObject;
            Log.Fatal(exception, "Domain unhandled exception");
        };
    }
}
