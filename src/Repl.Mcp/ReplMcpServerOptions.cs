using ModelContextProtocol.Protocol;
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
	/// Context name for the MCP subcommands (default: <c>"mcp"</c>).
	/// The serve subcommand becomes: <c>myapp {ContextName} serve</c>.
	/// </summary>
	public string ContextName { get; set; } = "mcp";

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
	/// When <c>true</c> (default), commands annotated <c>.ReadOnly()</c> are automatically
	/// exposed as MCP resources in addition to being tools.
	/// Set to <c>false</c> to require explicit <c>.AsResource()</c> marking.
	/// </summary>
	public bool AutoPromoteReadOnlyToResources { get; set; } = true;

	/// <summary>
	/// When <c>true</c>, resources are also exposed as read-only tools.
	/// This is a compatibility fallback for clients that don't support MCP resources (~61% as of March 2025).
	/// Default is <c>false</c> — opt in when your target agents lack resource support.
	/// </summary>
	public bool ResourceFallbackToTools { get; set; }

	/// <summary>
	/// Optional factory for creating custom MCP transports (e.g. WebSocket, SSE).
	/// When <c>null</c> (default), the server uses <c>StdioServerTransport</c>.
	/// The factory receives the server name and the I/O context for stream access.
	/// </summary>
	public Func<string, IReplIoContext, ITransport>? TransportFactory { get; set; }

	/// <summary>
	/// When <c>true</c>, prompts are also exposed as tools.
	/// This is a compatibility fallback for clients that don't support MCP prompts (~62% as of March 2025).
	/// Default is <c>false</c> — opt in when your target agents lack prompt support.
	/// </summary>
	public bool PromptFallbackToTools { get; set; }

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
