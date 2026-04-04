using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;

namespace Repl.Mcp;

/// <summary>
/// Custom <see cref="McpServerPrompt"/> subclass that derives prompt arguments from
/// the Repl documentation model and dispatches through the Repl pipeline.
/// </summary>
internal sealed class ReplMcpServerPrompt : McpServerPrompt
{
	private readonly McpToolAdapter _adapter;
	private readonly ReplDocCommand _command;
	private readonly Prompt _protocolPrompt;

	public ReplMcpServerPrompt(
		ReplDocCommand command,
		string promptName,
		McpToolAdapter adapter)
	{
		_adapter = adapter;
		_command = command;

		// Build prompt arguments from the command's route arguments and options.
		var arguments = new List<PromptArgument>();
		foreach (var arg in command.Arguments)
		{
			arguments.Add(new PromptArgument
			{
				Name = arg.Name,
				Description = arg.Description,
				Required = false, // MCP spec: all prompt arguments must be optional.
			});
		}

		foreach (var opt in command.Options)
		{
			arguments.Add(new PromptArgument
			{
				Name = opt.Name,
				Description = opt.Description,
				Required = false,
			});
		}

		_protocolPrompt = new Prompt
		{
			Name = promptName,
			Description = command.Description,
			Arguments = arguments,
		};
	}

	/// <inheritdoc />
	public override Prompt ProtocolPrompt => _protocolPrompt;

	/// <inheritdoc />
	public override IReadOnlyList<object> Metadata { get; } = [];

	/// <inheritdoc />
	public override async ValueTask<GetPromptResult> GetAsync(
		RequestContext<GetPromptRequestParams> request,
		CancellationToken cancellationToken = default)
	{
		// Prompt arguments are already JsonElement — pass through directly.
		var jsonArgs = request.Params.Arguments is { } args
			? new Dictionary<string, System.Text.Json.JsonElement>(args, StringComparer.Ordinal)
			: new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);

		var result = await _adapter.InvokeAsync(
			_protocolPrompt.Name, jsonArgs, request.Server, progressToken: null, cancellationToken)
			.ConfigureAwait(false);

		// Surface errors as MCP exceptions so clients can distinguish failures.
		if (result.IsError == true)
		{
			var errorText = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text
				?? "Prompt execution failed.";
			throw new McpException(errorText);
		}

		var text = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text
			?? _command.Description
			?? _protocolPrompt.Name;

		return new GetPromptResult
		{
			Messages =
			[
				new PromptMessage
				{
					Role = Role.User,
					Content = new TextContentBlock { Text = text },
				},
			],
		};
	}
}
