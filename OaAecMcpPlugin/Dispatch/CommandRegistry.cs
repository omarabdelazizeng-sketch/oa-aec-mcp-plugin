using OaAecMcpPlugin.Commands;

namespace OaAecMcpPlugin.Dispatch;

/// <summary>
///     Maps JSON-RPC method names to their <see cref="ICommand"/> implementations.
///     Thread-safe for reads after all commands are registered at startup.
/// </summary>
public class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.Ordinal);

    /// <summary>Register a command. Must be called on startup before the WebSocket server starts.</summary>
    public void Register(ICommand command) => _commands[command.Name] = command;

    /// <summary>
    ///     Try to look up a command by method name.
    ///     Returns false (and sets <paramref name="command"/> to null) if not registered.
    /// </summary>
    public bool TryGet(string name, out ICommand? command) =>
        _commands.TryGetValue(name, out command);
}
