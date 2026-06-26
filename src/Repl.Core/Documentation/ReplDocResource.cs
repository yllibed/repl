namespace Repl.Documentation;

/// <summary>
/// Resource metadata for commands marked as data sources.
/// </summary>
public sealed record ReplDocResource(
	string Path,
	string? Description,
	string? Details,
	IReadOnlyList<ReplDocArgument> Arguments,
	IReadOnlyList<ReplDocOption> Options)
{
	/// <summary>
	/// Gets the explicit MIME type override to advertise when this resource is exposed through MCP.
	/// When null, MCP consumers should use the active output transformer's MIME type.
	/// </summary>
	public string? MimeType { get; init; }
}
