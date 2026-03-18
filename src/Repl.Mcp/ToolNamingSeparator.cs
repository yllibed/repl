namespace Repl;

/// <summary>
/// Separator style for flattening context paths into MCP tool names.
/// </summary>
public enum ToolNamingSeparator
{
	/// <summary>Underscore: <c>contact_add</c>.</summary>
	Underscore,

	/// <summary>Slash: <c>contact/add</c>.</summary>
	Slash,

	/// <summary>Dot: <c>contact.add</c>.</summary>
	Dot,
}
