using ModelContextProtocol.AspNetCore;
using Repl.Mcp;

namespace Repl.Mcp.AspNetCore;

/// <summary>
/// Configures Repl MCP over ASP.NET Core Streamable HTTP.
/// </summary>
public sealed class ReplMcpHttpOptions
{
	/// <summary>
	/// Gets or sets a callback for configuring the Repl MCP server surface created for each MCP session.
	/// </summary>
	public Action<ReplMcpServerOptions>? ConfigureServer { get; set; }

	/// <summary>
	/// Gets or sets a callback for configuring the underlying MCP HTTP transport.
	/// </summary>
	public Action<HttpServerTransportOptions>? ConfigureTransport { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether MCP authorization filters should be registered.
	/// </summary>
	public bool EnableAuthorizationFilters { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether HTTP sessions should be stateless.
	/// </summary>
	public bool Stateless { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether a single execution context should be used per MCP session.
	/// </summary>
	public bool PerSessionExecutionContext { get; set; }

	/// <summary>
	/// Gets or sets the amount of idle time after which a stateful MCP session expires.
	/// </summary>
	public TimeSpan? IdleTimeout { get; set; }

	/// <summary>
	/// Gets or sets the maximum number of idle stateful MCP sessions to keep in memory.
	/// </summary>
	public int? MaxIdleSessionCount { get; set; }
}
