namespace Repl.Mcp;

/// <summary>
/// Rendering and security metadata for an MCP App UI resource.
/// </summary>
public sealed class McpAppResourceOptions
{
	/// <summary>
	/// Human-readable resource name shown by hosts that list app resources.
	/// </summary>
	public string? Name { get; set; }

	/// <summary>
	/// Optional description of the UI resource.
	/// </summary>
	public string? Description { get; set; }

	/// <summary>
	/// Content Security Policy domains requested by the UI.
	/// </summary>
	public McpAppCsp? Csp { get; set; }

	/// <summary>
	/// Browser permissions requested by the UI.
	/// </summary>
	public McpAppPermissions? Permissions { get; set; }

	/// <summary>
	/// Optional host-specific dedicated origin for the UI.
	/// </summary>
	public string? Domain { get; set; }

	/// <summary>
	/// Optional visual boundary preference.
	/// </summary>
	public bool? PrefersBorder { get; set; }

	/// <summary>
	/// Optional preferred display mode requested by the UI.
	/// Standard values are available from <see cref="McpAppDisplayModes"/>.
	/// Hosts decide which display modes they support.
	/// </summary>
	public string? PreferredDisplayMode { get; set; }

	/// <summary>
	/// Additional host-specific <c>_meta.ui</c> fields.
	/// Use this for experimental or host-specific presentation options.
	/// </summary>
	public IDictionary<string, string> UiMetadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
