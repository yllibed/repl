using ModelContextProtocol.AspNetCore;
using Repl.Mcp;

namespace Repl.Mcp.AspNetCore;

/// <summary>
/// Configures the self-hosted Repl MCP Streamable HTTP server.
/// </summary>
public sealed class ReplMcpHttpServerOptions
{
	/// <summary>
	/// Default local-only host.
	/// </summary>
	public const string DefaultHost = "127.0.0.1";

	/// <summary>
	/// Default HTTP port. The digits correspond to "repl" on a phone keypad.
	/// </summary>
	public const int DefaultPort = 7375;

	/// <summary>
	/// Default Streamable HTTP endpoint path.
	/// </summary>
	public const string DefaultPath = "/mcp";

	/// <summary>
	/// Gets or sets the hostname or IP address to bind.
	/// </summary>
	public string Host { get; set; } = DefaultHost;

	/// <summary>
	/// Gets or sets the HTTP port to bind.
	/// </summary>
	public int Port { get; set; } = DefaultPort;

	/// <summary>
	/// Gets or sets the Streamable HTTP endpoint path.
	/// </summary>
	public string Path { get; set; } = DefaultPath;

	/// <summary>
	/// Gets or sets a value indicating whether non-loopback bindings are allowed.
	/// </summary>
	public bool AllowRemote { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether startup messages should be suppressed.
	/// </summary>
	public bool Quiet { get; set; }

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

	internal ReplMcpHttpOptions ToHttpOptions() => new()
	{
		ConfigureServer = ConfigureServer,
		ConfigureTransport = ConfigureTransport,
		EnableAuthorizationFilters = EnableAuthorizationFilters,
		Stateless = Stateless,
		PerSessionExecutionContext = PerSessionExecutionContext,
		IdleTimeout = IdleTimeout,
		MaxIdleSessionCount = MaxIdleSessionCount,
	};

	internal ReplMcpHttpServerOptions Clone() => new()
	{
		Host = Host,
		Port = Port,
		Path = Path,
		AllowRemote = AllowRemote,
		Quiet = Quiet,
		ConfigureServer = ConfigureServer,
		ConfigureTransport = ConfigureTransport,
		EnableAuthorizationFilters = EnableAuthorizationFilters,
		Stateless = Stateless,
		PerSessionExecutionContext = PerSessionExecutionContext,
		IdleTimeout = IdleTimeout,
		MaxIdleSessionCount = MaxIdleSessionCount,
	};
}
