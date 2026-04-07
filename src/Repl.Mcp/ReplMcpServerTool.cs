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

	// MCP Tasks are experimental in the SDK (MCPEXP001) but part of the MCP spec.
	// LongRunning commands advertise optional task support so agents can use
	// the call-now/poll-later pattern instead of blocking on slow operations.
#pragma warning disable MCPEXP001
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
			Annotations = McpSchemaGenerator.MapAnnotations(command.Annotations),
			Execution = command.Annotations?.LongRunning == true
				? new ToolExecution { TaskSupport = ToolTaskSupport.Optional }
				: null,
			Meta = TryGetAppOptions(command, out var appOptions)
				? McpAppMetadata.BuildToolMeta(appOptions)
				: null,
		};
	}
#pragma warning restore MCPEXP001

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
