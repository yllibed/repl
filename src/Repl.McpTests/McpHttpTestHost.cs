using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Repl.McpTests;

internal sealed class McpHttpTestHost(WebApplication application) : IAsyncDisposable
{
	public Uri Endpoint { get; } = new(new Uri(application.Urls.Single()), "/mcp");

	public static async Task<McpHttpTestHost> StartAsync(
		Action<IServiceProvider, McpServerOptions> configureProjection,
		Action<IServiceCollection>? configureServices = null,
		Action<WebApplication>? configureApplication = null)
	{
		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
		builder.Logging.ClearProviders();
		configureServices?.Invoke(builder.Services);
		builder.Services.AddMcpServer().WithHttpTransport(options =>
		{
			options.SessionMode = HttpServerSessionMode.Stateless;
			options.ConfigureSessionOptions = (context, target, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				configureProjection(context.RequestServices, target);
				return Task.CompletedTask;
			};
		});

		var application = builder.Build();
		try
		{
			configureApplication?.Invoke(application);
			application.MapMcp("/mcp");
			await application.StartAsync().ConfigureAwait(false);
			return new McpHttpTestHost(application);
		}
		catch
		{
			await application.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public Task<McpClient> ConnectAsync(CancellationToken cancellationToken) =>
		McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = Endpoint,
				TransportMode = HttpTransportMode.StreamableHttp,
			}),
			cancellationToken: cancellationToken);

	public async ValueTask DisposeAsync()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		try
		{
			await application.StopAsync(timeout.Token).ConfigureAwait(false);
		}
		finally
		{
			await application.DisposeAsync().ConfigureAwait(false);
		}
	}
}
