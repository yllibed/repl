namespace Repl.Mcp;

/// <summary>
/// Content Security Policy domains requested by an MCP App resource.
/// </summary>
public sealed record McpAppCsp
{
	/// <summary>
	/// Origins allowed for fetch, XHR, and WebSocket connections.
	/// </summary>
	public IReadOnlyList<string>? ConnectDomains { get; init; }

	/// <summary>
	/// Origins allowed for images, scripts, stylesheets, fonts, and media.
	/// </summary>
	public IReadOnlyList<string>? ResourceDomains { get; init; }

	/// <summary>
	/// Origins allowed for nested iframes.
	/// </summary>
	public IReadOnlyList<string>? FrameDomains { get; init; }

	/// <summary>
	/// Origins allowed as document base URIs.
	/// </summary>
	public IReadOnlyList<string>? BaseUriDomains { get; init; }
}
