using Microsoft.Extensions.DependencyInjection;

namespace Repl.Mcp;

/// <summary>
/// What the MCP SDK consults to tell an explicitly registered prompt's injected parameters from its
/// client-supplied arguments.
/// </summary>
/// <remarks>
/// The SDK asks this whether a parameter's type is a service, and resolves a type reported here from the
/// session's services. It answers as those services do, with one exception: <see cref="IServiceProvider"/>
/// is not reported. Left unreported, an <see cref="IServiceProvider"/> parameter is bound to the SDK's own
/// request provider instead, which wraps the session's services and adds the request's: the server, the
/// request context, its progress reporter and the caller's identity. The exception lives here rather than
/// on the session's services, which every other consumer, <c>ActivatorUtilities</c> among them, also asks.
/// </remarks>
internal sealed class McpPromptParameterServices(IServiceProvider services)
	: IServiceProvider, IServiceProviderIsService
{
	public object? GetService(Type serviceType) =>
		serviceType == typeof(IServiceProviderIsService) ? this : services.GetService(serviceType);

	public bool IsService(Type serviceType) =>
		serviceType != typeof(IServiceProvider)
		&& (services.GetService(typeof(IServiceProviderIsService)) as IServiceProviderIsService)?.IsService(serviceType) == true;
}
