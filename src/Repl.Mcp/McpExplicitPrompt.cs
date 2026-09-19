using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Repl.Mcp;

/// <summary>
/// Gives an explicitly registered prompt the execution prologue every command-backed path gets.
/// </summary>
/// <remarks>
/// A prompt registered through <c>options.Prompt(...)</c> is a raw SDK primitive: the SDK invokes its
/// handler directly, so it never passes through <see cref="McpToolAdapter"/>. Without this it reaches
/// the handler with the connection's roots unresolved — a handler reading
/// <see cref="IMcpClientRoots.Current"/> would see an empty list and take it for a client that
/// declared no workspace — and with no buffer open, so on <c>2026-07-28</c> a request that declared no
/// log level loses its feedback silently, having no notification channel to receive it on.
/// <para>
/// <see cref="McpAppResource"/> carries the same prologue for the same reason. Those two are the
/// prebuilt primitives that bypass the adapter; everything command-backed gets it from the adapter
/// itself.
/// </para>
/// </remarks>
internal sealed class McpExplicitPrompt(
	McpServerPrompt inner,
	IServiceProvider services,
	McpRequestServerAccessor servers) : McpServerPrompt
{
	public override Prompt ProtocolPrompt => inner.ProtocolPrompt;

	public override IReadOnlyList<object> Metadata => inner.Metadata;

	public override async ValueTask<GetPromptResult> GetAsync(
		RequestContext<GetPromptRequestParams> request,
		CancellationToken cancellationToken = default)
	{
		// The reusable-options path dispatches straight into this prompt, so without binding here a
		// handler injecting a capability service sees no flowing request and reports the client as
		// incapable.
		servers.BindRequest(request);

		// The SDK resolves this handler's parameters from the request's own provider, and nothing on
		// this path sets one — so a prompt injecting a capability service could not be invoked at all.
		// Every other execution path reaches the handler through the adapter, which supplies them.
		request.Services = services;

		await McpClientRootsService.PrimeFromServicesAsync(services, cancellationToken).ConfigureAwait(false);
		var feedbackService = services.GetService(typeof(IMcpFeedback)) as McpFeedbackService;
		using var undelivered = feedbackService?.PushUndeliveredMessages();

		GetPromptResult result;
		try
		{
			result = await inner.GetAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (undelivered is not null
			&& (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
		{
			// A failure is exactly when the notices leading up to it are worth having, and a failed
			// prompt has no result left to carry them.
			var failed = undelivered.Messages.Drain();
			if (failed.Count == 0)
			{
				throw;
			}

			var surfaced = exception is McpException ? exception.Message : "Prompt execution failed.";
			throw new McpException(McpToolAdapter.AppendMessages(surfaced, failed), exception);
		}

		var drained = undelivered?.Messages.Drain() ?? [];
		if (drained.Count == 0)
		{
			return result;
		}

		// After the payload, as on the tool and command-backed prompt paths.
		var messages = new List<PromptMessage>(result.Messages.Count + drained.Count);
		messages.AddRange(result.Messages);
		foreach (var message in drained)
		{
			messages.Add(new PromptMessage
			{
				Role = Role.User,
				Content = new TextContentBlock { Text = message },
			});
		}

		return new GetPromptResult { Messages = messages, Description = result.Description };
	}
}
