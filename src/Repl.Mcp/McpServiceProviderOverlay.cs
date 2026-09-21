using Microsoft.Extensions.DependencyInjection;

namespace Repl.Mcp;

/// <summary>
/// Service provider overlay that injects MCP-specific services.
/// </summary>
/// <remarks>
/// It answers <see cref="IServiceProviderIsService"/> as well as <see cref="IServiceProvider"/>,
/// because a consumer that asks whether a type is available before asking for it would otherwise never
/// see what this overlay adds. The MCP SDK does exactly that when it decides whether a handler
/// parameter is a dependency or a client-supplied argument, so without this a prompt declaring
/// <c>IMcpFeedback</c> is classified as taking an argument named "feedback" and cannot be invoked at
/// all.
/// </remarks>
internal sealed class McpServiceProviderOverlay(
	IServiceProvider inner,
	IReadOnlyDictionary<Type, object> overrides) : IServiceProvider, IServiceProviderIsService
{
	public object? GetService(Type serviceType)
	{
		if (serviceType == typeof(IServiceProviderIsService))
		{
			return this;
		}

		if (overrides.TryGetValue(serviceType, out var service))
		{
			return service;
		}

		return inner.GetService(serviceType);
	}

	public bool IsService(Type serviceType) =>
		overrides.ContainsKey(serviceType)
		|| (inner.GetService(typeof(IServiceProviderIsService)) as IServiceProviderIsService)?.IsService(serviceType) == true;
}
