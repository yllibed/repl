using Repl.Interaction;

namespace Repl.Mcp;

/// <summary>
/// Service provider overlay that injects MCP-specific services (interaction channel).
/// </summary>
internal sealed class McpServiceProviderOverlay(
	IServiceProvider inner,
	IReplInteractionChannel interactionChannel) : IServiceProvider
{
	public object? GetService(Type serviceType)
	{
		if (serviceType == typeof(IReplInteractionChannel))
		{
			return interactionChannel;
		}

		return inner.GetService(serviceType);
	}
}
