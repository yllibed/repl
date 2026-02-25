namespace Repl.Protocol;

/// <summary>
/// Factory methods for protocol-level machine-readable contracts.
/// </summary>
public static class ProtocolContracts
{
	/// <summary>
	/// Creates a help document from scope and command metadata.
	/// </summary>
	/// <param name="scope">Scope path.</param>
	/// <param name="commands">Discoverable command metadata.</param>
	/// <returns>A new help document.</returns>
	public static HelpDocument CreateHelpDocument(
		string scope,
		IEnumerable<HelpCommand> commands)
	{
		scope = string.IsNullOrWhiteSpace(scope)
			? throw new ArgumentException("Scope cannot be empty.", nameof(scope))
			: scope;
		ArgumentNullException.ThrowIfNull(commands);

		return new HelpDocument(
			scope,
			commands.ToArray(),
			DateTimeOffset.UtcNow);
	}

	/// <summary>
	/// Creates a structured protocol error.
	/// </summary>
	/// <param name="code">Error code.</param>
	/// <param name="message">Error message.</param>
	/// <returns>A protocol error object.</returns>
	public static ProtocolError CreateError(string code, string message)
	{
		code = string.IsNullOrWhiteSpace(code)
			? throw new ArgumentException("Code cannot be empty.", nameof(code))
			: code;
		message = string.IsNullOrWhiteSpace(message)
			? throw new ArgumentException("Message cannot be empty.", nameof(message))
			: message;

		return new ProtocolError(code, message);
	}

	/// <summary>
	/// Creates an MCP tool descriptor from a help command.
	/// </summary>
	/// <param name="command">Help command metadata.</param>
	/// <returns>A mapped MCP tool descriptor.</returns>
	public static McpTool CreateMcpTool(HelpCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);

		var toolName = string.IsNullOrWhiteSpace(command.Name)
			? throw new ArgumentException("Command name cannot be empty.", nameof(command))
			: command.Name;
		var description = string.IsNullOrWhiteSpace(command.Description)
			? "No description."
			: command.Description;
		var schema = new
		{
			type = "object",
			properties = new { },
			required = Array.Empty<string>(),
			additionalProperties = false,
		};

		return new McpTool(toolName, description, schema);
	}

	/// <summary>
	/// Creates MCP tools from a help document using the command list as source.
	/// </summary>
	/// <param name="helpDocument">Help document.</param>
	/// <returns>Mapped MCP tools.</returns>
	public static IReadOnlyList<McpTool> CreateMcpTools(HelpDocument helpDocument)
	{
		ArgumentNullException.ThrowIfNull(helpDocument);

		return helpDocument.Commands
			.Select(CreateMcpTool)
			.ToArray();
	}

	/// <summary>
	/// Creates an MCP manifest for future multi-host integrations.
	/// </summary>
	/// <param name="name">Server/display name.</param>
	/// <param name="version">Manifest version.</param>
	/// <param name="tools">Tool descriptors.</param>
	/// <param name="resources">Resource descriptors.</param>
	/// <returns>A machine-readable MCP manifest.</returns>
	public static McpManifest CreateMcpManifest(
		string name,
		string version,
		IEnumerable<McpTool> tools,
		IEnumerable<McpResource>? resources = null)
	{
		name = string.IsNullOrWhiteSpace(name)
			? throw new ArgumentException("Name cannot be empty.", nameof(name))
			: name;
		version = string.IsNullOrWhiteSpace(version)
			? throw new ArgumentException("Version cannot be empty.", nameof(version))
			: version;
		ArgumentNullException.ThrowIfNull(tools);

		return new McpManifest(
			name,
			version,
			tools.ToArray(),
			(resources ?? Enumerable.Empty<McpResource>()).ToArray(),
			DateTimeOffset.UtcNow);
	}
}
