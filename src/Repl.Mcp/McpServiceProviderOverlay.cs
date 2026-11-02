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
/// all. For an explicitly registered prompt the SDK asks through <see cref="McpPromptParameterServices"/>,
/// which answers as this overlay does except for <see cref="IServiceProvider"/>.
/// <para>
/// It answers <see cref="IServiceProvider"/> with itself, since handing back the provider it wraps
/// would give a caller that asks for the provider a container that has never heard of the services
/// added here.
/// </para>
/// <para>
/// Keyed lookups go to the wrapped provider, since nothing here is keyed. Without that, a container that
/// supports keyed services would look, through the overlay, as if it did not.
/// </para>
/// </remarks>
internal sealed class McpServiceProviderOverlay(
	IServiceProvider inner,
	IReadOnlyDictionary<Type, object> overrides) : IServiceProvider, IKeyedServiceProvider, IServiceProviderIsService
{
	public object? GetService(Type serviceType)
	{
		if (serviceType == typeof(IServiceProviderIsService) || serviceType == typeof(IServiceProvider))
		{
			return this;
		}

		if (overrides.TryGetValue(serviceType, out var service))
		{
			return service;
		}

		return inner.GetService(serviceType);
	}

	public object? GetKeyedService(Type serviceType, object? serviceKey) =>
		serviceKey is null
			? GetService(serviceType)
			: (inner as IKeyedServiceProvider)?.GetKeyedService(serviceType, serviceKey);

	public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
		GetKeyedService(serviceType, serviceKey)
		?? throw new InvalidOperationException($"No service of type '{serviceType}' with key '{serviceKey}' is registered.");

	// Kept in step with GetService, as the IServiceProviderIsService contract requires: what this overlay
	// answers itself counts, whatever the wrapped provider says about it.
	public bool IsService(Type serviceType) =>
		serviceType == typeof(IServiceProvider)
		|| serviceType == typeof(IServiceProviderIsService)
		|| overrides.ContainsKey(serviceType)
		|| (inner.GetService(typeof(IServiceProviderIsService)) as IServiceProviderIsService)?.IsService(serviceType) == true;
}
