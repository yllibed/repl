namespace Repl.Mcp;

/// <summary>
/// Standard MCP Apps display mode values.
/// </summary>
public static class McpAppDisplayModes
{
	/// <summary>Render the app inline in the conversation surface.</summary>
	public const string Inline = "inline";

	/// <summary>Render the app in a fullscreen presentation surface, when supported by the host.</summary>
	public const string Fullscreen = "fullscreen";

	/// <summary>Render the app in picture-in-picture mode, when supported by the host.</summary>
	public const string PictureInPicture = "pip";
}
