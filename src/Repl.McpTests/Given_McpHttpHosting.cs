using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpHttpHosting
{
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
	[TestMethod]
	[Description("The SDK HTTP transport negotiates the modern protocol and executes a parameterized Repl command.")]
	public async Task When_AReplCatalogIsHostedStatelessly_Then_ARealClientCanDiscoverAndCallIt()
	{
		var app = ReplApp.Create();
		app.Map("greet {name}", (string name) => $"Hello, {name}!");
		string? protocolVersion = null;
		await using var host = await McpHttpTestHost.StartAsync(
			(services, target) => app.ConfigureMcpServerOptions(target, services: services),
			configureApplication: web => web.Use(async (context, next) =>
			{
				protocolVersion = context.Request.Headers["MCP-Protocol-Version"];
				await next(context).ConfigureAwait(false);
			})).ConfigureAwait(false);
		using var timeout = new CancellationTokenSource(RequestTimeout);
		var client = await host.ConnectAsync(timeout.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);

		var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
		tools.Should().ContainSingle(tool => tool.Name == "greet");
		var result = await client.CallToolAsync("greet",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "HTTP" }, cancellationToken: timeout.Token).ConfigureAwait(false);

		result.IsError.Should().NotBeTrue();
		result.Content.OfType<TextContentBlock>().Should().ContainSingle().Which.Text.Should().Contain("Hello, HTTP!");
		protocolVersion.Should().Be("2026-07-28");
	}

	[TestMethod]
	[Description("Core projections expose all three primitive kinds without requiring an application service provider.")]
	public async Task When_CoreHasNoApplicationDi_Then_HttpServesToolsResourcesAndPrompts()
	{
		var app = CoreReplApp.Create();
		app.Map("ping", () => "pong");
		app.Map("config", () => "configuration").AsResource();
		app.Map("guide", () => "helpful instructions").AsPrompt();
		await using var host = await McpHttpTestHost.StartAsync(
			(_, target) => app.ConfigureMcpServerOptions(target)).ConfigureAwait(false);
		using var timeout = new CancellationTokenSource(RequestTimeout);
		var client = await host.ConnectAsync(timeout.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);

		var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
		tools.Should().ContainSingle().Which.Name.Should().Be("ping");
		var result = await client.CallToolAsync("ping", cancellationToken: timeout.Token).ConfigureAwait(false);

		Text(result).Should().Contain("pong");

		var resources = await client.ListResourcesAsync(cancellationToken: timeout.Token).ConfigureAwait(false);

		resources.Should().ContainSingle();

		var resource = await client.ReadResourceAsync(resources[0].Uri, cancellationToken: timeout.Token).ConfigureAwait(false);

		resource.Contents.OfType<TextResourceContents>().Should().ContainSingle().Which.Text.Should().Contain("configuration");

		var prompts = await client.ListPromptsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);

		prompts.Should().ContainSingle();

		var prompt = await client.GetPromptAsync(prompts[0].Name, cancellationToken: timeout.Token).ConfigureAwait(false);

		prompt.Messages.Should().ContainSingle().Which.Content.Should().BeOfType<TextContentBlock>()
			.Which.Text.Should().Contain("helpful instructions");
	}

	[TestMethod]
	[Description("Filtering removes a command from discovery and prevents HTTP invocation through its name or alias.")]
	public async Task When_ACommandIsFilteredOut_Then_DirectAndAliasCallsCannotExecuteIt()
	{
		var executions = 0;
		var app = ReplApp.Create();
		app.Map("allowed", () => "visible");
		app.Map("admin", () => Interlocked.Increment(ref executions)).WithAlias("wipe");
		await using var host = await McpHttpTestHost.StartAsync((services, target) =>
			app.ConfigureMcpServerOptions(target, options =>
			{
				options.CommandFilter = command => string.Equals(command.Path, "allowed", StringComparison.Ordinal);
			}, services)).ConfigureAwait(false);
		using var timeout = new CancellationTokenSource(RequestTimeout);
		var client = await host.ConnectAsync(timeout.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);

		var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
		tools.Should().ContainSingle().Which.Name.Should().Be("allowed");
		foreach (var name in new[] { "admin", "wipe" })
		{
			var invoke = async () => await client.CallToolAsync(name,
				cancellationToken: timeout.Token).ConfigureAwait(false);
			await invoke.Should().ThrowAsync<McpException>().ConfigureAwait(false);
		}

		executions.Should().Be(0);
	}

	[TestMethod]
	[DataRow(false, DisplayName = "Core uses the HTTP request scope")]
	[DataRow(true, DisplayName = "Defaults uses the HTTP request scope")]
	[Description("Concurrent Core and Defaults invocations borrow the middleware request scope while retaining shared singleton state.")]
	public async Task When_RequestsRunConcurrently_Then_TheyUseTheirHttpScopeAndShareOnlySingletons(bool useDefaults)
	{
		var probes = new ConcurrentBag<RequestProbe>();
		var shared = new SharedProbe();
		var app = ReplApp.Create();
		app.Map("scope", async (RequestProbe probe, SharedProbe singleton, CancellationToken cancellationToken) =>
		{
			await Task.Delay(30, cancellationToken).ConfigureAwait(false);
			return $"{probe.Id}|{probe.MiddlewareId}|{singleton.Id}";
		});
		await using var host = await McpHttpTestHost.StartAsync((services, target) =>
			{
				if (useDefaults)
					app.ConfigureMcpServerOptions(target, services: services);
				else
					app.Core.ConfigureMcpServerOptions(target, services: services);
			},
			services =>
			{
				services.AddSingleton(shared);
				services.AddScoped(_ =>
				{
					var probe = new RequestProbe();
					probes.Add(probe);
					return probe;
				});
			},
			web => web.Use(async (context, next) =>
			{
				var probe = context.RequestServices.GetRequiredService<RequestProbe>();
				probe.MiddlewareId = probe.Id;
				await next(context).ConfigureAwait(false);
			})).ConfigureAwait(false);
		using var timeout = new CancellationTokenSource(RequestTimeout);
		var client = await host.ConnectAsync(timeout.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);

		var results = await Task.WhenAll(
			client.CallToolAsync("scope", cancellationToken: timeout.Token).AsTask(),
			client.CallToolAsync("scope", cancellationToken: timeout.Token).AsTask()).ConfigureAwait(false);
		var identities = results.Select(result => Text(result).Trim('"').Split('|')).ToArray();

		identities[0][0].Should().NotBe(identities[1][0]);
		foreach (var identity in identities)
		{
			identity[0].Should().Be(identity[1], "the command and HTTP middleware must use the same scope");
			identity[2].Should().Be(shared.Id.ToString());
		}

#pragma warning disable VSTHRD003 // HTTP requests complete these test synchronization gates.
		await Task.WhenAll(probes.Select(probe => probe.Disposed.Task)).WaitAsync(timeout.Token).ConfigureAwait(false);
#pragma warning restore VSTHRD003
	}

	[TestMethod]
	[Description("Request cancellation and command errors release asynchronous scoped dependencies without interrupting healthy calls.")]
	public async Task When_ARequestFailsOrIsCancelled_Then_ScopedServicesAreDisposedAndOtherCallsSurvive()
	{
		var probes = new ConcurrentBag<RequestProbe>();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var app = ReplApp.Create();
		app.Map("wait", async (RequestProbe probe, CancellationToken cancellationToken) =>
		{
			entered.TrySetResult();
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
				return probe.Id.ToString();
			}
			finally
			{
				probe.Cancelled = cancellationToken.IsCancellationRequested;
			}
		});
		app.Map("fail", (RequestProbe probe) => Results.Error("failed", $"failure {probe.Id}"));
		app.Map("ok", (RequestProbe probe) => probe.Id.ToString());
		await using var host = await McpHttpTestHost.StartAsync(
			(services, target) => app.ConfigureMcpServerOptions(target, services: services),
			services => services.AddScoped(_ =>
			{
				var probe = new RequestProbe();
				probes.Add(probe);
				return probe;
			})).ConfigureAwait(false);
		using var timeout = new CancellationTokenSource(RequestTimeout);
		var client = await host.ConnectAsync(timeout.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);
		using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
		var waiting = client.CallToolAsync("wait", cancellationToken: cancelled.Token).AsTask();
#pragma warning disable VSTHRD003 // HTTP requests complete these test synchronization gates.
		await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
#pragma warning restore VSTHRD003
		var healthy = await client.CallToolAsync("ok", cancellationToken: timeout.Token).ConfigureAwait(false);

		healthy.IsError.Should().NotBeTrue();

		await cancelled.CancelAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003 // HTTP requests complete these test synchronization gates.
		var observeCancellation = async () => await waiting.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		await observeCancellation.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		var failure = await client.CallToolAsync("fail", cancellationToken: timeout.Token).ConfigureAwait(false);

		failure.IsError.Should().BeTrue();
		Text(failure).Should().Contain("failure");

#pragma warning disable VSTHRD003 // HTTP requests complete these test synchronization gates.
		await Task.WhenAll(probes.Select(probe => probe.Disposed.Task)).WaitAsync(timeout.Token).ConfigureAwait(false);
#pragma warning restore VSTHRD003
		probes.Should().Contain(probe => probe.Cancelled);
	}

	[TestMethod]
	[Description("A failed projection callback releases its HTTP scope and leaves the host available for subsequent requests.")]
	public async Task When_ProjectionConfigurationThrows_Then_TheHttpScopeIsReleased()
	{
		var probe = new RequestProbe();
		var configureCalls = 0;
		var app = ReplApp.Create();
		app.Map("ok", () => "ready");
		await using var host = await McpHttpTestHost.StartAsync((services, target) =>
			{
				if (Interlocked.Increment(ref configureCalls) == 1)
				{
					services.GetRequiredService<RequestProbe>().Should().BeSameAs(probe);
					throw new InvalidOperationException("projection failed");
				}
				app.ConfigureMcpServerOptions(target, services: services);
			},
			services => services.AddScoped(_ => probe)).ConfigureAwait(false);
		using var timeout = new CancellationTokenSource(RequestTimeout);
		using var http = new HttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, host.Endpoint);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		request.Content = new StringContent("""
			{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"failure-probe","version":"1"}}}
			""", Encoding.UTF8, "application/json");

		using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
		response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
#pragma warning disable VSTHRD003 // The HTTP request owns and disposes this test probe.
		await probe.Disposed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
#pragma warning restore VSTHRD003
		var client = await host.ConnectAsync(timeout.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);
		Text(await client.CallToolAsync("ok", cancellationToken: timeout.Token).ConfigureAwait(false)).Should().Contain("ready");
	}

	[TestMethod]
	[Description("The Defaults fallback resolves dependencies from the application provider rather than the HTTP host.")]
	public async Task When_DefaultsProviderIsOmitted_Then_ApplicationServicesAreUsed()
	{
		var applicationProbe = new SharedProbe();
		var app = ReplApp.Create(services => services.AddSingleton(applicationProbe));
		app.Map("id", (SharedProbe probe) => probe.Id.ToString());
		await using var host = await McpHttpTestHost.StartAsync(
			(_, target) => app.ConfigureMcpServerOptions(target),
			services => services.AddSingleton<SharedProbe>()).ConfigureAwait(false);
		using var timeout = new CancellationTokenSource(RequestTimeout);
		var client = await host.ConnectAsync(timeout.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);

		var result = await client.CallToolAsync("id", cancellationToken: timeout.Token).ConfigureAwait(false);

		Text(result).Should().Contain(applicationProbe.Id.ToString());
	}

	private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

	private sealed class SharedProbe
	{
		public Guid Id { get; } = Guid.NewGuid();
	}

	private sealed class RequestProbe : IAsyncDisposable
	{
		public Guid Id { get; } = Guid.NewGuid();
		public Guid MiddlewareId { get; set; }
		public bool Cancelled { get; set; }
		public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ValueTask DisposeAsync()
		{
			Disposed.TrySetResult();
			return ValueTask.CompletedTask;
		}
	}
}
