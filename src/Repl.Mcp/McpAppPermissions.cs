namespace Repl.Mcp;

/// <summary>
/// Browser permissions requested by an MCP App resource.
/// </summary>
public sealed record McpAppPermissions
{
	/// <summary>Requests camera access.</summary>
	public bool Camera { get; init; }

	/// <summary>Requests microphone access.</summary>
	public bool Microphone { get; init; }

	/// <summary>Requests geolocation access.</summary>
	public bool Geolocation { get; init; }

	/// <summary>Requests clipboard write access.</summary>
	public bool ClipboardWrite { get; init; }
}
