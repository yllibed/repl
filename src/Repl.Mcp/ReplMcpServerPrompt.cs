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
				Required = arg.Required,
			});
		}

		foreach (var opt in command.Options)
		{
			arguments.Add(new PromptArgument
			{
				Name = opt.Name,
				Description = opt.Description,
				Required = opt.Required,
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
		_adapter.BindRequest(request);

		// Prompt arguments are already JsonElement — pass through directly.
		var jsonArgs = request.Params.Arguments is { } args
			? new Dictionary<string, System.Text.Json.JsonElement>(args, StringComparer.Ordinal)
			: new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);

		var result = await _adapter.InvokeAsync(
			_protocolPrompt.Name, jsonArgs, request.Server, progressToken: null, cancellationToken)
			.ConfigureAwait(false);

		// Materialised once, and before the error branch: the adapter appends any message the client
		// could not receive as a notification after the payload, and a prompt that fails must not drop
		// them either — a failure is exactly when the notices leading up to it are worth having.
		var blocks = result.Content?.OfType<TextContentBlock>().ToArray() ?? [];

		// Surface errors as MCP exceptions so clients can distinguish failures.
		if (result.IsError == true)
		{
			throw new McpException(McpToolAdapter.BuildErrorMessage(blocks, "Prompt execution failed."));
		}

		var outputText = blocks.Length > 0 ? blocks[0].Text : null;
		var text = outputText is null
			? _command.Description ?? _protocolPrompt.Name
			: McpJsonStringOutput.UnwrapJsonStringLiteral(outputText);

		var messages = new List<PromptMessage>(Math.Max(blocks.Length, 1))
		{
			new()
			{
				Role = Role.User,
				Content = new TextContentBlock { Text = text },
			},
		};

		// Buffered feedback rides after the payload, as it does on the tool path. Keeping only the
		// first block made prompts/get the one caller that silently discarded it.
		for (var i = 1; i < blocks.Length; i++)
		{
			messages.Add(new PromptMessage { Role = Role.User, Content = blocks[i] });
		}

		return new GetPromptResult { Messages = messages };
	}
}
