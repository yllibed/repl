using ModelContextProtocol.Server;

namespace Repl.Mcp;

/// <summary>
/// Represents a dynamically generated MCP server session for custom transports.
/// </summary>
public sealed class ReplMcpServerSession : IDisposable
{
	private readonly McpServerHandler _handler;
	private bool _disposed;

	internal ReplMcpServerSession(McpServerHandler handler)
	{
		_handler = handler;
		ServerOptions = handler.BuildDynamicServerOptions();
		Services = handler.SessionServices;
	}

	/// <summary>
	/// Gets the MCP server options for this session.
	/// </summary>
	public McpServerOptions ServerOptions { get; }

	/// <summary>
	/// Gets the session-aware service provider for this session.
	/// </summary>
	public IServiceProvider Services { get; }

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_handler.Dispose();
		_disposed = true;
	}
}
