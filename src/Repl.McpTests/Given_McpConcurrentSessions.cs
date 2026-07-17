using System.IO.Pipelines;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

// One test exercises Sampling, deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005)
// but still supported by Repl.Mcp until the SDK removes the surface (#51).
#pragma warning disable MCP9005

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpConcurrentSessions
{
	[TestMethod]
	[Description("Guards capability binding against cross-session interference: with one handler serving two sessions (SDK 2.0 binds a destination server per request), a paused call from a sampling-capable client must still observe ITS OWN client's capabilities after a request from a sampling-less client has been served — the capability services must bind to the flowing request, not to a shared last-attached server.")]
	public async Task When_TwoClientsWithDifferentCapabilitiesShareHandler_Then_CapabilityBindingIsPerRequest()
	{
		using var entered = new SemaphoreSlim(0, 1);
		using var gate = new SemaphoreSlim(0, 1);

		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("probe", async (IMcpSampling sampling) =>
		{
			var before = sampling.IsSupported;
			entered.Release();
			await gate.WaitAsync().ConfigureAwait(false);
			var after = sampling.IsSupported;
			return $"{before}|{after}";
		});
		app.Map("poke", () => "ok");

		var options = new ReplMcpServerOptions
		{
			TransportFactory = static (serverName, io) => new StreamServerTransport(
				((McpTestFixture.PipeIoContext)io).InputStream,
				((McpTestFixture.PipeIoContext)io).OutputStream,
				serverName),
		};
		var handler = new McpServerHandler(app.Core, options, McpTestFixture.EmptyServices);
		using var cts = new CancellationTokenSource();

		var (clientA, serverTaskA) = await StartSessionAsync(handler, BuildSamplingClientOptions(), cts.Token).ConfigureAwait(false);
		var (clientB, serverTaskB) = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);

		// Session A enters "probe" (sampling supported) and pauses on the gate; session B is
		// then served in full; A resumes and must STILL see its own sampling capability.
		var probeTask = clientA.CallToolAsync(
			"probe", new Dictionary<string, object?>(StringComparer.Ordinal), cancellationToken: cts.Token);
		(await entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)).Should().BeTrue();

		await clientB.CallToolAsync(
			"poke", new Dictionary<string, object?>(StringComparer.Ordinal), cancellationToken: cts.Token)
			.ConfigureAwait(false);

		gate.Release();
		var probeResult = await probeTask.ConfigureAwait(false);

		var text = probeResult.Content.OfType<TextContentBlock>().First().Text;
		text.Should().Contain("True|True");

		await clientA.DisposeAsync().ConfigureAwait(false);
		await clientB.DisposeAsync().ConfigureAwait(false);
		await cts.CancelAsync().ConfigureAwait(false);
		_ = serverTaskA;
		_ = serverTaskB;
	}

	private static McpClientOptions BuildSamplingClientOptions() => new()
	{
		Capabilities = new ClientCapabilities { Sampling = new SamplingCapability() },
		Handlers = new McpClientHandlers
		{
			SamplingHandler = static (request, _, _) => ValueTask.FromResult(new CreateMessageResult
			{
				Content = [new TextContentBlock { Text = "ga" }],
				Model = "test-model",
			}),
		},
	};

	private static async Task<(McpClient Client, Task ServerTask)> StartSessionAsync(
		McpServerHandler handler,
		McpClientOptions? clientOptions,
		CancellationToken cancellationToken)
	{
		var clientToServer = new Pipe();
		var serverToClient = new Pipe();
		var io = new McpTestFixture.PipeIoContext(
			clientToServer.Reader.AsStream(),
			serverToClient.Writer.AsStream());
		var serverTask = handler.RunAsync(io, cancellationToken);

		var clientTransport = new StreamClientTransport(
			clientToServer.Writer.AsStream(),
			serverToClient.Reader.AsStream());
		var client = await McpClient.CreateAsync(clientTransport, clientOptions, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		return (client, serverTask);
	}

	[TestMethod]
	[Description("Two independent MCP sessions can run concurrently without interference.")]
	public async Task When_TwoSessionsRunConcurrently_Then_EachSeesOwnTools()
	{
		var session1 = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("alpha", () => "a");
		}).ConfigureAwait(false);

		await using (session1.ConfigureAwait(false))
		{
			var session2 = await McpTestFixture.CreateAsync(app =>
			{
				app.Map("beta", () => "b");
				app.Map("gamma", () => "c");
			}).ConfigureAwait(false);

			await using (session2.ConfigureAwait(false))
			{
				var tools1 = await session1.Client.ListToolsAsync().ConfigureAwait(false);
				var tools2 = await session2.Client.ListToolsAsync().ConfigureAwait(false);

				tools1.Should().ContainSingle(t => string.Equals(t.Name, "alpha", StringComparison.Ordinal));
				tools1.Should().NotContain(t => string.Equals(t.Name, "beta", StringComparison.Ordinal));

				tools2.Should().NotContain(t => string.Equals(t.Name, "alpha", StringComparison.Ordinal));
				tools2.Should().HaveCount(2);
			}
		}
	}

	[TestMethod]
	[Description("Concurrent tool invocations on separate sessions do not cross-contaminate output.")]
	public async Task When_ToolsInvokedConcurrently_Then_OutputIsIsolated()
	{
		var session1 = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("echo {msg}", (string msg) => $"s1:{msg}");
		}).ConfigureAwait(false);

		await using (session1.ConfigureAwait(false))
		{
			var session2 = await McpTestFixture.CreateAsync(app =>
			{
				app.Map("echo {msg}", (string msg) => $"s2:{msg}");
			}).ConfigureAwait(false);

			await using (session2.ConfigureAwait(false))
			{
				var result1 = await session1.Client.CallToolAsync(
					"echo", new Dictionary<string, object?>(StringComparer.Ordinal) { ["msg"] = "hello" })
					.ConfigureAwait(false);
				var result2 = await session2.Client.CallToolAsync(
					"echo", new Dictionary<string, object?>(StringComparer.Ordinal) { ["msg"] = "hello" })
					.ConfigureAwait(false);

				var text1 = result1.Content.OfType<TextContentBlock>().First().Text;
				var text2 = result2.Content.OfType<TextContentBlock>().First().Text;

				text1.Should().Contain("s1:hello");
				text2.Should().Contain("s2:hello");
			}
		}
	}
}
