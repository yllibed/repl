using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;

namespace Repl.Mcp;

internal sealed class ReplMcpAppLauncherTool : McpServerTool
{
	private readonly Tool _protocolTool;
	private readonly string _fallbackText;

	public ReplMcpAppLauncherTool(
		ReplDocCommand command,
		string toolName,
		McpAppCommandResourceOptions options)
	{
		_fallbackText = BuildFallbackText(command, options);
		_protocolTool = new Tool
		{
			Name = toolName,
			Description = McpSchemaGenerator.BuildDescription(command),
			InputSchema = McpSchemaGenerator.BuildInputSchema(command),
			Annotations = McpSchemaGenerator.MapAnnotations(command.Annotations),
			Meta = McpAppMetadata.BuildToolMeta(
				new McpAppToolOptions(options.ResourceUri) { Visibility = options.Visibility }),
		};
	}

	public override Tool ProtocolTool => _protocolTool;

	public override IReadOnlyList<object> Metadata { get; } = [];

	public override ValueTask<CallToolResult> InvokeAsync(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(new CallToolResult
		{
			Content = [new TextContentBlock { Text = _fallbackText }],
		});

	private static string BuildFallbackText(
		ReplDocCommand command,
		McpAppCommandResourceOptions options) =>
		BuildFallbackTextCore(command, options);

	internal static string BuildFallbackTextCore(
		ReplDocCommand command,
		McpAppCommandResourceOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.ResourceOptions.LauncherText))
		{
			return options.ResourceOptions.LauncherText;
		}

		if (!string.IsNullOrWhiteSpace(command.Description))
		{
			return command.Description;
		}

		var name = options.ResourceOptions.Name ?? command.Path;
		return $"Opening {name}.";
	}
}
