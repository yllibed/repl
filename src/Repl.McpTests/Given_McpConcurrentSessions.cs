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

	[TestMethod]
	[Description("Guards root isolation across sessions sharing one handler: the hard-roots cache must be keyed by session, otherwise the second root-capable client silently receives the FIRST client's workspace roots — a cross-session data exposure — instead of its own roots/list round-trip.")]
	public async Task When_TwoRootCapableClientsShareHandler_Then_EachSeesOwnRoots()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("roots", async (IMcpClientRoots roots, CancellationToken ct) =>
			string.Join(',', (await roots.GetAsync(ct).ConfigureAwait(false)).Select(root => root.Uri.ToString())));

		var options = new ReplMcpServerOptions
		{
			TransportFactory = static (serverName, io) => new StreamServerTransport(
				((McpTestFixture.PipeIoContext)io).InputStream,
				((McpTestFixture.PipeIoContext)io).OutputStream,
				serverName),
		};
		var handler = new McpServerHandler(app.Core, options, McpTestFixture.EmptyServices);
		using var cts = new CancellationTokenSource();

		var (clientA, _) = await StartSessionAsync(handler, BuildRootsClientOptions("file:///ga"), cts.Token).ConfigureAwait(false);
		var (clientB, _) = await StartSessionAsync(handler, BuildRootsClientOptions("file:///bu"), cts.Token).ConfigureAwait(false);

		var resultA = await clientA.CallToolAsync(
			toolName: "roots",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);
		var resultB = await clientB.CallToolAsync(
			toolName: "roots",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);

		resultA.Content.OfType<TextContentBlock>().First().Text.Should().Contain("file:///ga");
		var textB = resultB.Content.OfType<TextContentBlock>().First().Text;
		textB.Should().Contain("file:///bu");
		textB.Should().NotContain("file:///ga");

		await clientA.DisposeAsync().ConfigureAwait(false);
		await clientB.DisposeAsync().ConfigureAwait(false);
		await cts.CancelAsync().ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Guards routing-notification lifetime across sessions: when the first-attached session closes, the surviving session must still receive tools/list_changed after a routing invalidation — session attachment must be reference-counted, not first-wins with a handler-wide unsubscribe on first close.")]
	public async Task When_FirstSessionCloses_Then_SurvivingSessionStillReceivesRoutingNotifications()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("alpha", () => "a");

		var options = new ReplMcpServerOptions
		{
			TransportFactory = static (serverName, io) => new StreamServerTransport(
				((McpTestFixture.PipeIoContext)io).InputStream,
				((McpTestFixture.PipeIoContext)io).OutputStream,
				serverName),
		};
		var handler = new McpServerHandler(app.Core, options, McpTestFixture.EmptyServices);
		using var ctsA = new CancellationTokenSource();
		using var ctsB = new CancellationTokenSource();

		var (clientA, serverTaskA) = await StartSessionAsync(handler, clientOptions: null, ctsA.Token).ConfigureAwait(false);
		var (clientB, _) = await StartSessionAsync(handler, clientOptions: null, ctsB.Token).ConfigureAwait(false);

		var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var registration = clientB.RegisterNotificationHandler(
			NotificationMethods.ToolListChangedNotification,
			(_, _) =>
			{
				listChanged.TrySetResult();
				return ValueTask.CompletedTask;
			});
		await using var _ = registration.ConfigureAwait(false);

		// Both sessions are live; close the FIRST one, then invalidate routing.
		await clientA.DisposeAsync().ConfigureAwait(false);
		await ctsA.CancelAsync().ConfigureAwait(false);
		try
		{
			await serverTaskA.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Expected: session A's RunAsync ends on cancellation.
		}

		app.Map("late", () => "l");
		app.Core.InvalidateRouting();

		await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		await clientB.DisposeAsync().ConfigureAwait(false);
		await ctsB.CancelAsync().ConfigureAwait(false);
	}

	private static McpClientOptions BuildRootsClientOptions(string rootUri) => new()
	{
		Capabilities = new ClientCapabilities
		{
			Roots = new RootsCapability { ListChanged = true },
		},
		Handlers = new McpClientHandlers
		{
			RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult
			{
				Roots = [new Root { Uri = rootUri, Name = rootUri }],
			}),
		},
	};

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
