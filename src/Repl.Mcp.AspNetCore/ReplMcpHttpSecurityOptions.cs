namespace Repl.Mcp.AspNetCore;

/// <summary>
/// Configures defensive HTTP checks for self-hosted Repl MCP endpoints.
/// </summary>
public sealed class ReplMcpHttpSecurityOptions
{
	/// <summary>
	/// Gets or sets the allowed HTTP Host header values. Ports are ignored when matching.
	/// </summary>
	public IList<string> AllowedHosts { get; } =
	[
		"localhost",
		"127.0.0.1",
		"::1",
		"[::1]",
	];

	/// <summary>
	/// Gets the allowed browser Origin header values.
	/// </summary>
	public IList<string> AllowedOrigins { get; } = [];

	/// <summary>
	/// Gets or sets a value indicating whether any HTTP Host header should be accepted.
	/// </summary>
	public bool AllowAnyHost { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether any browser Origin header should be accepted.
	/// </summary>
	public bool AllowAnyOrigin { get; set; }
}
