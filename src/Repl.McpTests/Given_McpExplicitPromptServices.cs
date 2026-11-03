using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// What a prompt registered with <see cref="ReplMcpServerOptions.Prompt"/> can resolve, on each way of hosting it.
/// </summary>
[TestClass]
public sealed class Given_McpExplicitPromptServices
{
	private const string McpServe = "mcp serve";
	private const string ReusableOptions = "reusable options";
	private const string ReusableOptionsWithoutAppProvider = "reusable options without an app provider";
	private const string KeyedServiceKey = "probe-key";

	[TestMethod]
	[DataRow(McpServe)]
	[DataRow(ReusableOptions)]
	[DataRow(ReusableOptionsWithoutAppProvider)]
	[Description("An explicitly registered prompt can declare an MCP capability service as a parameter. Issue #96 reported this failing the prompts/get request outright; McpExplicitPrompt's replacement of request.Services already resolved it on every path, and this pins that it stays so.")]
	public async Task When_AnExplicitPromptInjectsSampling_Then_ItResolves(string hosting)
	{
		var text = await GetPromptTextAsync(hosting, static (IMcpSampling sampling) => sampling is null ? "null" : "resolved")
			.ConfigureAwait(false);

		text.Should().Be("resolved");
	}

	[TestMethod]
	[DataRow(McpServe)]
	[DataRow(ReusableOptions)]
	[DataRow(ReusableOptionsWithoutAppProvider)]
	[Description("A prompt that declares IServiceProvider and resolves a capability service from it must find it. Wherever the app's container reported IServiceProvider as a service, the SDK resolved the parameter from the session's services, and the overlay answered with that container, which knows nothing of IMcpSampling, IMcpClientRoots, IMcpElicitation or IMcpFeedback.")]
	public async Task When_AnExplicitPromptResolvesACapabilityThroughItsServiceProvider_Then_ItIsFound(string hosting)
	{
		var text = await GetPromptTextAsync(
				hosting,
				static (IServiceProvider services) => services.GetService(typeof(IMcpSampling)) is null ? "sp-null" : "sp-ok")
			.ConfigureAwait(false);

		text.Should().Be("sp-ok");
	}

	[TestMethod]
	[DataRow(McpServe)]
	[DataRow(ReusableOptions)]
	[DataRow(ReusableOptionsWithoutAppProvider)]
	[Description("A handler that passes its provider on, to ActivatorUtilities or a factory, hands over whatever the provider answers for IServiceProvider. The request provider forwards that question to the session's services, so the overlay must answer with itself rather than with the container it wraps, or the capability services are lost one step away.")]
	public async Task When_AnExplicitPromptResolvesACapabilityThroughTheProviderItResolves_Then_ItIsFound(string hosting)
	{
		var text = await GetPromptTextAsync(
				hosting,
				static (IServiceProvider services) =>
					services.GetRequiredService<IServiceProvider>().GetService(typeof(IMcpSampling)) is null ? "sp-null" : "sp-ok")
			.ConfigureAwait(false);

		text.Should().Be("sp-ok");
	}

	[TestMethod]
	[DataRow(McpServe)]
	[DataRow(ReusableOptions)]
	[DataRow(ReusableOptionsWithoutAppProvider)]
	[Description("A prompt's IServiceProvider is the SDK's request provider, which adds the request's own services to the session's: McpServer, RequestContext, IProgress and ClaimsPrincipal. The SDK hands a prompt that provider only when the services it consults do not report IServiceProvider as a service; reporting it made the SDK resolve the parameter from the session's services instead, and a handler resolving the server through it got null.")]
	public async Task When_AnExplicitPromptResolvesTheServerThroughItsServiceProvider_Then_ItIsFound(string hosting)
	{
		var text = await GetPromptTextAsync(
				hosting,
				static (IServiceProvider services) => services.GetService(typeof(McpServer)) is null ? "server-null" : "server-ok")
			.ConfigureAwait(false);

		text.Should().Be("server-ok");
	}

	[TestMethod]
	[DataRow(McpServe)]
	[DataRow(ReusableOptions)]
	[Description("A prompt can resolve a keyed service through its IServiceProvider. The overlay sits between the SDK and the app's container, so unless it passes keyed lookups on, a container that supports them looks as if it did not, and GetKeyedService either finds nothing or throws.")]
	public async Task When_AnExplicitPromptResolvesAKeyedServiceThroughItsServiceProvider_Then_ItIsFound(string hosting)
	{
		var text = await GetPromptTextAsync(
				hosting,
				static (IServiceProvider services) => services.GetKeyedService<string>(KeyedServiceKey) ?? "keyed-null",
				static services => services.AddKeyedSingleton(KeyedServiceKey, "keyed-ok"))
			.ConfigureAwait(false);

		text.Should().Be("keyed-ok");
	}

	private static async Task<string> GetPromptTextAsync(
		string hosting,
		Delegate handler,
		Action<IServiceCollection>? configureServices = null)
	{
		switch (hosting)
		{
			case McpServe:
				var fixture = await McpTestFixture.CreateAsync(
						_ => { },
						options => options.Prompt("probe", handler),
						clientOptions: null,
						configureServices)
					.ConfigureAwait(false);
				await using (fixture.ConfigureAwait(false))
				{
					return await ReadPromptAsync(fixture.Client).ConfigureAwait(false);
				}
			case ReusableOptions:
				var app = configureServices is null ? ReplApp.Create() : ReplApp.Create(configureServices);
				return await ReadReusablePromptAsync(app.BuildMcpServerOptions(options => options.Prompt("probe", handler)))
					.ConfigureAwait(false);
			case ReusableOptionsWithoutAppProvider:
				// ICoreReplApp.BuildMcpServerOptions() wraps an empty provider, so the overlay alone supplies the
				// capability services.
				return await ReadReusablePromptAsync(
						ReplApp.Create().Core.BuildMcpServerOptions(options => options.Prompt("probe", handler)))
					.ConfigureAwait(false);
			default:
				throw new ArgumentOutOfRangeException(nameof(hosting), hosting, message: null);
		}
	}

	private static async Task<string> ReadReusablePromptAsync(McpServerOptions mcpOptions)
	{
		var session = await McpPipeSession.StartAsync(mcpOptions, clientOptions: null, CancellationToken.None)
			.ConfigureAwait(false);
		await using (session.ConfigureAwait(false))
		{
			return await ReadPromptAsync(session.Client).ConfigureAwait(false);
		}
	}

	private static async Task<string> ReadPromptAsync(McpClient client)
	{
		var result = await client.GetPromptAsync("probe", arguments: null).ConfigureAwait(false);
		return string.Join('\n', result.Messages.Select(static message => (message.Content as TextContentBlock)?.Text ?? string.Empty));
	}
}
