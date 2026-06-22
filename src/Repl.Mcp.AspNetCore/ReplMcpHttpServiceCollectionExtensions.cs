using Microsoft.AspNetCore.Http;
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
	private static readonly object SessionItemKey = new();

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
		var options = new ReplMcpHttpOptions();
		configure?.Invoke(options);
		return services.AddReplMcpHttp(app, options);
	}

	/// <summary>
	/// Registers Repl MCP server services using the MCP Streamable HTTP transport.
	/// </summary>
	/// <param name="services">Service collection.</param>
	/// <param name="app">The Repl app to expose over MCP.</param>
	/// <param name="options">HTTP MCP configuration.</param>
	/// <returns>The MCP server builder.</returns>
	public static IMcpServerBuilder AddReplMcpHttp(
		this IServiceCollection services,
		ReplApp app,
		ReplMcpHttpOptions options)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(app);
		ArgumentNullException.ThrowIfNull(options);

		services.TryAddSingleton(app);

		var builder = services.AddMcpServer();
		if (options.EnableAuthorizationFilters)
		{
			builder.AddAuthorizationFilters();
		}

		builder.WithHttpTransport(http => ConfigureTransport(http, app, options));

		return builder;
	}

	internal static void ConfigureTransport(
		HttpServerTransportOptions http,
		ReplApp app,
		ReplMcpHttpOptions options)
	{
		ApplyTransportOptions(http, options);
		options.ConfigureTransport?.Invoke(http);
		ConfigureSessionOptions(http, app, options);
		ConfigureRunSessionHandler(http);
	}

	private static void ConfigureSessionOptions(
		HttpServerTransportOptions http,
		ReplApp app,
		ReplMcpHttpOptions options)
	{
		var configureSessionOptions = http.ConfigureSessionOptions;
		http.ConfigureSessionOptions = async (context, serverOptions, cancellationToken) =>
		{
			var sessionServices = new CompositeServiceProvider(context.RequestServices, app.Services);
			var session = app.CreateMcpServerSession(sessionServices, options.ConfigureServer);
			try
			{
				CopyServerOptions(session.ServerOptions, serverOptions);
				context.Items[SessionItemKey] = session;

				if (configureSessionOptions is not null)
				{
					await configureSessionOptions(context, serverOptions, cancellationToken).ConfigureAwait(false);
				}
			}
			catch
			{
				context.Items.Remove(SessionItemKey);
				session.Dispose();
				throw;
			}
		};
	}

	private static void ConfigureRunSessionHandler(HttpServerTransportOptions http)
	{
#pragma warning disable MCPEXP002
		var runSessionHandler = http.RunSessionHandler;
		http.RunSessionHandler = async (context, server, cancellationToken) =>
		{
			var session = TakeSession(context);
			ReplMcpHttpDiagnostics.SessionsStarted.Add(1);
			ReplMcpHttpDiagnostics.SessionsActive.Add(1);
			try
			{
				if (runSessionHandler is not null)
				{
					await runSessionHandler(context, server, cancellationToken).ConfigureAwait(false);
				}
				else
				{
					await server.RunAsync(cancellationToken).ConfigureAwait(false);
				}
			}
			finally
			{
				session ??= TakeSession(context);
				session?.Dispose();
				ReplMcpHttpDiagnostics.SessionsEnded.Add(1);
				ReplMcpHttpDiagnostics.SessionsActive.Add(-1);
			}
		};
#pragma warning restore MCPEXP002
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

	private static ReplMcpServerSession? TakeSession(HttpContext context)
	{
		if (context.Items.TryGetValue(SessionItemKey, out var value)
			&& value is ReplMcpServerSession session)
		{
			context.Items.Remove(SessionItemKey);
			return session;
		}

		return null;
	}
}
