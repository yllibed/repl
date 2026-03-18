using Repl.Documentation;

namespace Repl;

/// <summary>
/// Configuration for the Repl MCP server integration.
/// </summary>
/// <remarks>
/// Named <c>ReplMcpServerOptions</c> to avoid collision with the SDK's
/// <c>ModelContextProtocol.Server.McpServerOptions</c>.
/// </remarks>
public sealed class ReplMcpServerOptions
{
	/// <summary>
	/// Server name reported in the MCP <c>initialize</c> response.
	/// Defaults to the assembly product name.
	/// </summary>
	public string? ServerName { get; set; }

	/// <summary>
	/// Server version reported in the MCP <c>initialize</c> response.
	/// </summary>
	public string? ServerVersion { get; set; }

	/// <summary>
	/// Command name for the MCP context (default: <c>"mcp"</c>).
	/// The serve subcommand becomes: <c>myapp {CommandName} serve</c>.
	/// </summary>
	public string CommandName { get; set; } = "mcp";

	/// <summary>
	/// Separator used when flattening context paths into MCP tool names.
	/// </summary>
	public ToolNamingSeparator ToolNamingSeparator { get; set; } = ToolNamingSeparator.Underscore;

	/// <summary>
	/// Controls how runtime interaction prompts are handled in MCP mode.
	/// </summary>
	public InteractivityMode InteractivityMode { get; set; } = InteractivityMode.PrefillThenFail;

	/// <summary>
	/// Optional filter controlling which commands are exposed as MCP tools.
	/// When <c>null</c>, all non-hidden, non-<see cref="CommandAnnotations.AutomationHidden"/> commands are exposed.
	/// </summary>
	public Func<ReplDocCommand, bool>? CommandFilter { get; set; }

	/// <summary>
	/// When <c>true</c> (default), resources are also exposed as read-only tools when the client
	/// does not advertise resource support in its capabilities during the MCP <c>initialize</c> handshake.
	/// </summary>
	public bool ResourceFallbackToTools { get; set; } = true;

	private readonly List<McpPromptRegistration> _prompts = [];

	/// <summary>
	/// Registers an MCP prompt with a DI-injectable handler.
	/// </summary>
	/// <param name="name">Prompt name (must be unique).</param>
	/// <param name="handler">Handler delegate.</param>
	/// <returns>The same options instance.</returns>
	public ReplMcpServerOptions Prompt(string name, Delegate handler)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(handler);
		_prompts.Add(new McpPromptRegistration(name, handler));
		return this;
	}

	/// <summary>
	/// Gets the registered prompt definitions.
	/// </summary>
	internal IReadOnlyList<McpPromptRegistration> Prompts => _prompts;
}
