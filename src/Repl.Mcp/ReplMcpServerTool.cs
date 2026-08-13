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
	private readonly bool _isLongRunning;

	// The modern MCP Tasks extension selects the execution mode from this marker. The
	// wire protocol no longer carries the retired per-tool Tool.Execution augmentation.
	public ReplMcpServerTool(
		ReplDocCommand command,
		string toolName,
		McpToolAdapter adapter)
	{
		_adapter = adapter;
		_isLongRunning = command.Annotations?.LongRunning == true;
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

	/// <summary>Whether this tool opts into the modern MCP Tasks runtime.</summary>
	internal bool IsLongRunning => _isLongRunning;

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
