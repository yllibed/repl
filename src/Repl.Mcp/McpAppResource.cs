using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Repl.Mcp;

internal sealed class McpAppResource : McpServerResource
{
	private readonly McpAppResourceRegistration _registration;
	private readonly IServiceProvider _services;
	private readonly McpRequestServerAccessor _servers;
	private readonly ResourceTemplate _protocolResourceTemplate;

	public McpAppResource(
		McpAppResourceRegistration registration,
		IServiceProvider services,
		McpRequestServerAccessor servers)
	{
		_registration = registration;
		_services = services;
		_servers = servers;
		_protocolResourceTemplate = new ResourceTemplate
		{
			Name = registration.Options.Name ?? registration.Uri,
			Description = registration.Options.Description,
			UriTemplate = registration.Uri,
			MimeType = McpAppValidation.ResourceMimeType,
			Meta = McpAppMetadata.BuildResourceMeta(registration.Options),
		};
	}

	public override ResourceTemplate ProtocolResourceTemplate => _protocolResourceTemplate;

	public override IReadOnlyList<object> Metadata { get; } = [];

	public override bool IsMatch(string uri) =>
		string.Equals(uri, _registration.Uri, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Surfaces a failed read together with whatever feedback the handler buffered, or leaves the
	/// exception alone when it buffered none.
	/// </summary>
	/// <remarks>
	/// A failed read has no body to carry what the handler reported, so the surfaced error is the only
	/// place left for it — the same treatment the command-backed resource paths give it. Only the
	/// app-authored feedback travels: the handler's own message stays behind, because it can name a path
	/// from an <see cref="IOException"/> or a parameter and its full CLR type from a binding failure.
	/// Withholding it is not a courtesy but a matter of not undoing the SDK, which flattens any
	/// non-<see cref="McpException"/> to "An error occurred." and passes an <see cref="McpException"/>'s
	/// message through verbatim — so wrapping is the act that would disclose it, and returning without
	/// throwing is what leaves the empty case sanitized. The same rule as
	/// <c>McpServerHandler.ThrowSanitizedIfAClientAlreadyHasASchema</c>.
	/// <para>
	/// An <see cref="McpException"/> is the exception to that: raising one is a deliberate act and its
	/// message was written for this client, which is why the SDK lets it through. Replacing it because
	/// the handler also reported something would make the feedback cost the explanation — and the same
	/// failure without feedback would explain itself, which no caller could account for.
	/// </para>
	/// </remarks>
	private static void ThrowIfFeedbackBuffered(
		Exception exception,
		McpFeedbackService.UndeliveredMessageScope undelivered)
	{
		var drained = undelivered.Messages.Drain();
		if (drained.Count == 0)
		{
			return;
		}

		var surfaced = exception is McpException ? exception.Message : "MCP App resource read failed.";
		throw new McpException(McpToolAdapter.AppendMessages(surfaced, drained), exception);
	}

	public override async ValueTask<ReadResourceResult> ReadAsync(
		RequestContext<ReadResourceRequestParams> request,
		CancellationToken cancellationToken = default)
	{
		// Like every other prebuilt primitive: the reusable-options path dispatches straight into this
		// resource, so without binding here a handler injecting IMcpClientRoots, IMcpSampling,
		// IMcpElicitation or IMcpFeedback sees no flowing request and reports the client as incapable.
		_servers.BindRequest(request);

		// This is the one primitive that does not run through McpToolAdapter, so the execution prologue
		// every command-backed path gets has to be repeated here: roots primed so a handler reading
		// Current sees them, and a buffer open so feedback the client cannot receive as a notification
		// is not simply lost.
		await McpClientRootsService.PrimeFromServicesAsync(_services, cancellationToken).ConfigureAwait(false);
		var feedbackService = _services.GetService(typeof(IMcpFeedback)) as McpFeedbackService;
		using var undelivered = feedbackService?.PushUndeliveredMessages();

		string html;
		try
		{
			html = await McpAppResourceInvoker
				.InvokeAsync(
					_registration.Handler,
					_services,
					new McpAppResourceContext(request.Params.Uri),
					request,
					cancellationToken)
				.ConfigureAwait(false);
		}
		// Cancellation is told apart by who asked for it, not by the exception's type: a handler that runs
		// its own budget and gives up reports a failure like any other and its feedback still matters. Only
		// the caller abandoning the request takes the bare path, which is the same rule
		// McpClientRootsService.PrimeFromServicesAsync applies.
		catch (Exception exception) when (undelivered is not null
			&& (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
		{
			ThrowIfFeedbackBuffered(exception, undelivered);
			throw;
		}

		return McpCacheHints.MarkPrivateToThisClient(request, new ReadResourceResult
		{
			Contents =
			[
				new TextResourceContents
				{
					Uri = request.Params.Uri,
					MimeType = McpAppValidation.ResourceMimeType,
					Text = html,
					Meta = McpAppMetadata.BuildResourceMeta(_registration.Options),
				},
			],
		});
	}
}
