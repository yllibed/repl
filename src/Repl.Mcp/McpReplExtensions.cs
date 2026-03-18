using Microsoft.Extensions.DependencyInjection;
using Repl.Mcp;

namespace Repl;

/// <summary>
/// Extension methods for integrating MCP server support into a Repl app.
/// </summary>
public static class McpReplExtensions
{
	/// <summary>
	/// Registers MCP server services in the DI container.
	/// </summary>
	/// <param name="services">Service collection.</param>
	/// <param name="configure">Optional configuration callback.</param>
	/// <returns>The same service collection.</returns>
	public static IServiceCollection AddReplMcpServer(
		this IServiceCollection services,
		Action<ReplMcpServerOptions>? configure = null)
	{
		var options = new ReplMcpServerOptions();
		configure?.Invoke(options);
		services.AddSingleton(options);
		return services;
	}

	/// <summary>
	/// Enables MCP server mode via <c>{commandName} serve</c>.
	/// </summary>
	/// <param name="app">Target Repl app.</param>
	/// <param name="configure">Optional configuration callback.</param>
	/// <returns>The same app instance.</returns>
	public static ReplApp UseMcpServer(
		this ReplApp app,
		Action<ReplMcpServerOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(app);

		var options = new ReplMcpServerOptions();
		configure?.Invoke(options);

		app.MapModule(
			new McpModule(options),
			static context => context.Channel is ReplRuntimeChannel.Cli);

		return app;
	}
}
