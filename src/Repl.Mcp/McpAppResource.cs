using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Repl.Mcp;

internal sealed class McpAppResource : McpServerResource
{
	private readonly McpAppResourceRegistration _registration;
	private readonly IServiceProvider _services;
	private readonly ResourceTemplate _protocolResourceTemplate;

	public McpAppResource(McpAppResourceRegistration registration, IServiceProvider services)
	{
		_registration = registration;
		_services = services;
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

	public override async ValueTask<ReadResourceResult> ReadAsync(
		RequestContext<ReadResourceRequestParams> request,
		CancellationToken cancellationToken = default)
	{
		var html = await McpAppResourceInvoker
			.InvokeAsync(
				_registration.Handler,
				_services,
				new McpAppResourceContext(request.Params.Uri),
				request,
				cancellationToken)
			.ConfigureAwait(false);

		return new ReadResourceResult
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
		};
	}
}
