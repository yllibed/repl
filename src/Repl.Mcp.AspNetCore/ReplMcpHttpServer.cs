using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Repl.Mcp.AspNetCore;

/// <summary>
/// Runs self-hosted Repl MCP Streamable HTTP servers.
/// </summary>
public static class ReplMcpHttpServer
{
	/// <summary>
	/// Runs a self-hosted Repl MCP Streamable HTTP server until cancellation is requested.
	/// </summary>
	/// <param name="app">The Repl app to expose over MCP.</param>
	/// <param name="configure">Optional server configuration callback.</param>
	/// <param name="output">Optional startup output writer.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public static async Task RunAsync(
		ReplApp app,
		Action<ReplMcpHttpServerOptions>? configure = null,
		TextWriter? output = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(app);

		var options = new ReplMcpHttpServerOptions();
		configure?.Invoke(options);

		await RunAsync(app, options, output, cancellationToken).ConfigureAwait(false);
	}

	internal static async Task RunAsync(
		ReplApp replApp,
		ReplMcpHttpServerOptions options,
		TextWriter? output,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(replApp);
		ArgumentNullException.ThrowIfNull(options);

		var binding = McpHttpBindingFactory.Create(
			options.Host,
			options.Port,
			options.Path,
			options.AllowRemote);

		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.AddSimpleConsole();
		options.ConfigureBuilder?.Invoke(builder);
		builder.WebHost.UseUrls(binding.ListenUrl);
		ApplyBindingSecurityDefaults(binding, options.Security);
		builder.Services.AddSingleton(options.Security);
		builder.Services.AddReplMcpHttp(replApp, options.Http);

		var webApp = builder.Build();
		await using (webApp.ConfigureAwait(false))
		{
			webApp.UseMiddleware<ReplMcpHttpSecurityMiddleware>();
			options.ConfigureApp?.Invoke(webApp);
			var endpoint = webApp.MapReplMcp(binding.Path);
			options.ConfigureEndpoint?.Invoke(endpoint);

			if (!options.Quiet && output is not null)
			{
				await output.WriteLineAsync($"MCP HTTP server listening on {binding.EndpointUrl}").ConfigureAwait(false);
				await output.WriteLineAsync(
					binding.AllowsRemote
						? "Remote clients are allowed by the selected binding."
						: "Remote clients are disabled; bind a non-loopback host with --allow-remote to expose it.")
					.ConfigureAwait(false);
			}

			await webApp.StartAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
			finally
			{
				await webApp.StopAsync(CancellationToken.None).ConfigureAwait(false);
			}
		}
	}

	internal static void ApplyBindingSecurityDefaults(
		McpHttpBinding binding,
		ReplMcpHttpSecurityOptions security)
	{
		if (binding.AllowsRemote && security.UsesDefaultAllowedHosts())
		{
			security.AllowAnyHost = true;
		}
	}
}
