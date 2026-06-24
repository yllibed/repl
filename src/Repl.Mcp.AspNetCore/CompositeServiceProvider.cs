namespace Repl.Mcp.AspNetCore;

internal sealed class CompositeServiceProvider(
	IServiceProvider primary,
	IServiceProvider fallback) : IServiceProvider
{
	public object? GetService(Type serviceType) =>
		primary.GetService(serviceType) ?? fallback.GetService(serviceType);
}
