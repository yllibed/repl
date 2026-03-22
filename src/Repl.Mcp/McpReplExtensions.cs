using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Repl.Mcp;

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

	/// <summary>
	/// Builds <see cref="McpServerOptions"/> from a <see cref="ReplApp"/>'s command graph,
	/// automatically using the app's shared service provider for DI during tool dispatch.
	/// Use this to integrate with custom transports (WebSocket, HTTP) or ASP.NET Core
	/// without going through the <c>mcp serve</c> CLI command.
	/// </summary>
	/// <param name="app">The Repl app.</param>
	/// <param name="configure">Optional MCP configuration callback.</param>
	/// <returns>Ready-to-use <see cref="McpServerOptions"/> for <c>McpServer.Create</c> or ASP.NET integration.</returns>
	public static McpServerOptions BuildMcpServerOptions(
		this ReplApp app,
		Action<ReplMcpServerOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(app);

		return app.Core.BuildMcpServerOptions(configure, app.Services);
	}

	/// <summary>
	/// Builds <see cref="McpServerOptions"/> from the Repl app's command graph.
	/// Use this to integrate with custom transports (WebSocket, HTTP) or ASP.NET Core
	/// without going through the <c>mcp serve</c> CLI command.
	/// The returned options capture the current command graph as pre-populated
	/// collections.
	/// </summary>
	/// <param name="app">The core Repl app.</param>
	/// <param name="configure">Optional MCP configuration callback.</param>
	/// <param name="services">Optional service provider for DI during tool dispatch.</param>
	/// <returns>Ready-to-use <see cref="McpServerOptions"/> for <c>McpServer.Create</c> or ASP.NET integration.</returns>
	public static McpServerOptions BuildMcpServerOptions(
		this ICoreReplApp app,
		Action<ReplMcpServerOptions>? configure = null,
		IServiceProvider? services = null)
	{
		ArgumentNullException.ThrowIfNull(app);

		var options = new ReplMcpServerOptions();
		configure?.Invoke(options);

		var handler = new McpServerHandler(app, options, services ?? EmptyServiceProvider.Instance);
		return handler.BuildStaticServerOptions();
	}

	private sealed class EmptyServiceProvider : IServiceProvider
	{
		public static readonly EmptyServiceProvider Instance = new();
		public object? GetService(Type serviceType) => null;
	}
}
