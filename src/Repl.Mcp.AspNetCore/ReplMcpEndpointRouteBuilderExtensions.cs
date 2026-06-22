using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Repl.Mcp.AspNetCore;

/// <summary>
/// Extension methods for mapping Repl MCP endpoints.
/// </summary>
public static class ReplMcpEndpointRouteBuilderExtensions
{
	/// <summary>
	/// Maps the Repl MCP Streamable HTTP endpoint.
	/// </summary>
	/// <param name="endpoints">Endpoint route builder.</param>
	/// <param name="pattern">Route pattern for the Streamable HTTP endpoint.</param>
	/// <returns>An endpoint convention builder.</returns>
	public static IEndpointConventionBuilder MapReplMcp(
		this IEndpointRouteBuilder endpoints,
		string pattern = "/mcp")
	{
		ArgumentNullException.ThrowIfNull(endpoints);
		return endpoints.MapMcp(pattern);
	}
}
