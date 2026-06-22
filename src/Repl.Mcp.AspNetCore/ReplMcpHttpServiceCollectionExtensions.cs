using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Repl.Mcp;

namespace Repl.Mcp.AspNetCore;

/// <summary>
/// Extension methods for registering Repl MCP over ASP.NET Core Streamable HTTP.
/// </summary>
public static class ReplMcpHttpServiceCollectionExtensions
{
	/// <summary>
	/// Registers Repl MCP server services using the MCP Streamable HTTP transport.
	/// </summary>
	/// <param name="services">Service collection.</param>
	/// <param name="app">The Repl app to expose over MCP.</param>
	/// <param name="configure">Optional HTTP MCP configuration callback.</param>
	/// <returns>The MCP server builder.</returns>
	public static IMcpServerBuilder AddReplMcpHttp(
		this IServiceCollection services,
		ReplApp app,
		Action<ReplMcpHttpOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(app);

		var options = new ReplMcpHttpOptions();
		configure?.Invoke(options);

		services.TryAddSingleton(app);

		var builder = services.AddMcpServer();
		if (options.EnableAuthorizationFilters)
		{
			builder.AddAuthorizationFilters();
		}

		builder.WithHttpTransport(http =>
		{
			ApplyTransportOptions(http, options);
			options.ConfigureTransport?.Invoke(http);

			var configureSessionOptions = http.ConfigureSessionOptions;
			http.ConfigureSessionOptions = async (context, serverOptions, cancellationToken) =>
			{
				var sessionServices = new CompositeServiceProvider(context.RequestServices, app.Services);
				var session = app.CreateMcpServerSession(sessionServices, options.ConfigureServer);
				CopyServerOptions(session.ServerOptions, serverOptions);

				if (configureSessionOptions is not null)
				{
					await configureSessionOptions(context, serverOptions, cancellationToken).ConfigureAwait(false);
				}
			};
		});

		return builder;
	}

	private static void ApplyTransportOptions(
		HttpServerTransportOptions transport,
		ReplMcpHttpOptions options)
	{
		transport.Stateless = options.Stateless;
		transport.PerSessionExecutionContext = options.PerSessionExecutionContext;

		if (options.IdleTimeout is { } idleTimeout)
		{
			transport.IdleTimeout = idleTimeout;
		}

		if (options.MaxIdleSessionCount is { } maxIdleSessionCount)
		{
			transport.MaxIdleSessionCount = maxIdleSessionCount;
		}
	}

	private static void CopyServerOptions(McpServerOptions source, McpServerOptions target)
	{
		target.ServerInfo = source.ServerInfo;
		target.Capabilities = source.Capabilities;
		target.Handlers = source.Handlers;
	}
}
