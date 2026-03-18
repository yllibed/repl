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
		var arguments = request.Params?.Arguments
			?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		var progressToken = request.Params?.ProgressToken;

		return await _adapter.InvokeAsync(
			_protocolTool.Name, arguments, request.Server, progressToken, cancellationToken)
			.ConfigureAwait(false);
	}
}
