namespace Repl.Mcp;

/// <summary>
/// Service provider overlay that injects MCP-specific services.
/// </summary>
internal sealed class McpServiceProviderOverlay(
	IServiceProvider inner,
	IReadOnlyDictionary<Type, object> overrides) : IServiceProvider
{
	public object? GetService(Type serviceType)
	{
		if (overrides.TryGetValue(serviceType, out var service))
		{
			return service;
		}

		return inner.GetService(serviceType);
	}
}
