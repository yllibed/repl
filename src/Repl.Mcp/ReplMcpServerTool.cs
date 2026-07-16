using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;

namespace Repl.Mcp;

/// <summary>
/// Custom <see cref="McpServerTool"/> subclass that generates schema from Repl's
/// documentation model and dispatches calls through the Repl pipeline.
/// </summary>
internal sealed class ReplMcpServerTool : McpServerTool
{
	private readonly McpToolAdapter _adapter;
	private readonly Tool _protocolTool;

	// SDK 2.0 removed the experimental MCP Tasks tool-augmentation surface
	// (Tool.Execution / ToolTaskSupport, MCPEXP001 in 1.x): the Tasks SEP was deferred out
	// of the 2.0 protocol release. Repl keeps .LongRunning() in its own model (help/docs);
	// advertising task support returns with the SDK's Tasks runtime (tracked in issue #51).
	public ReplMcpServerTool(
		ReplDocCommand command,
		string toolName,
		McpToolAdapter adapter)
	{
		_adapter = adapter;
		_protocolTool = new Tool
		{
			Name = toolName,
			Description = McpSchemaGenerator.BuildDescription(command),
			InputSchema = McpSchemaGenerator.BuildInputSchema(command),
			OutputSchema = McpSchemaGenerator.BuildOutputSchema(command),
			Annotations = McpSchemaGenerator.MapAnnotations(command.Annotations),
			Meta = TryGetAppOptions(command, out var appOptions)
				? McpAppMetadata.BuildToolMeta(appOptions)
				: null,
		};
	}

	/// <inheritdoc />
	public override Tool ProtocolTool => _protocolTool;

	/// <inheritdoc />
	public override IReadOnlyList<object> Metadata { get; } = [];

	/// <inheritdoc />
	public override async ValueTask<CallToolResult> InvokeAsync(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken = default)
	{
		var arguments = request.Params.Arguments
			?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		var progressToken = request.Params.ProgressToken;

		return await _adapter.InvokeAsync(
			_protocolTool.Name, arguments, request.Server, progressToken, cancellationToken)
			.ConfigureAwait(false);
	}

	private static bool TryGetAppOptions(ReplDocCommand command, out McpAppToolOptions options)
	{
		if (command.Metadata is not null
			&& command.Metadata.TryGetValue(McpAppMetadata.CommandMetadataKey, out var value)
			&& value is McpAppToolOptions appOptions)
		{
			options = appOptions;
			return true;
		}

		options = null!;
		return false;
	}
}
