using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpConcurrentSessions
{
	// The SDK's McpProtocolVersions constants are internal, so the revisions are pinned here.
	// 2026-07-28 (SEP-2567) is the sessionless, per-request-metadata revision the default client
	// negotiates; every guarantee in this file is written against it, hence the explicit assertions.
	private const string ModernProtocolVersion = "2026-07-28";

	[TestMethod]
	[Description("Pins the protocol revision every other guarantee in this file is written against: two sessions sharing one handler must both negotiate 2026-07-28. Without this, a silent fallback to the 2025-11-25 initialize handshake would make the per-session capability and catalog assertions below describe a revision they were never meant to characterise — which is exactly how four review waves missed the sessionless-protocol defects.")]
	public async Task When_TwoSessionsShareOneHandler_Then_BothNegotiateTheModernRevision()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("alpha", () => "a");
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();

		var sessionA = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scopeA = sessionA.ConfigureAwait(false);
		var sessionB = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scopeB = sessionB.ConfigureAwait(false);

		sessionA.Client.NegotiatedProtocolVersion.Should().Be(ModernProtocolVersion);
		sessionB.Client.NegotiatedProtocolVersion.Should().Be(ModernProtocolVersion);
	}

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
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();

		var sessionA = await StartSessionAsync(handler, BuildSamplingClientOptions(), cts.Token).ConfigureAwait(false);
		await using var scopeA = sessionA.ConfigureAwait(false);
		var sessionB = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scopeB = sessionB.ConfigureAwait(false);

		// Session A enters "probe" (sampling supported) and pauses on the gate; session B is
		// then served in full; A resumes and must STILL see its own sampling capability.
		var probeTask = sessionA.Client.CallToolAsync(
			"probe", new Dictionary<string, object?>(StringComparer.Ordinal), cancellationToken: cts.Token);
		(await entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)).Should().BeTrue();

		await sessionB.Client.CallToolAsync(
			"poke", new Dictionary<string, object?>(StringComparer.Ordinal), cancellationToken: cts.Token)
			.ConfigureAwait(false);

		gate.Release();
		var probeResult = await probeTask.ConfigureAwait(false);

		probeResult.Content.OfType<TextContentBlock>().First().Text.Should().Contain("True|True");
	}

	[TestMethod]
	[Description("Guards root isolation across sessions sharing one handler: the hard-roots cache must be keyed by session, otherwise the second root-capable client silently receives the FIRST client's workspace roots — a cross-session data exposure — instead of its own roots/list round-trip.")]
	public async Task When_TwoRootCapableClientsShareHandler_Then_EachSeesOwnRoots()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("roots", async (IMcpClientRoots roots, CancellationToken ct) =>
			string.Join(',', (await roots.GetAsync(ct).ConfigureAwait(false)).Select(root => root.Uri.ToString())));
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();

		var sessionA = await StartSessionAsync(handler, BuildRootsClientOptions("file:///ga"), cts.Token).ConfigureAwait(false);
		await using var scopeA = sessionA.ConfigureAwait(false);
		var sessionB = await StartSessionAsync(handler, BuildRootsClientOptions("file:///bu"), cts.Token).ConfigureAwait(false);
		await using var scopeB = sessionB.ConfigureAwait(false);

		var resultA = await sessionA.Client.CallToolAsync(
			toolName: "roots",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);
		var resultB = await sessionB.Client.CallToolAsync(
			toolName: "roots",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);

		resultA.Content.OfType<TextContentBlock>().First().Text.Should().Contain("file:///ga");
		var textB = resultB.Content.OfType<TextContentBlock>().First().Text;
		textB.Should().Contain("file:///bu");
		textB.Should().NotContain("file:///ga");
	}

	[TestMethod]
	[Description("Guards routing-notification lifetime across sessions: when the first-attached session closes, the surviving session must still receive tools/list_changed after a routing invalidation — session attachment must be reference-counted, not first-wins with a handler-wide unsubscribe on first close.")]
	public async Task When_FirstSessionCloses_Then_SurvivingSessionStillReceivesRoutingNotifications()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("alpha", () => "a");
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();

		var sessionA = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		var sessionB = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scopeB = sessionB.ConfigureAwait(false);

		var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var registration = sessionB.Client.RegisterNotificationHandler(
			NotificationMethods.ToolListChangedNotification,
			(_, _) =>
			{
				listChanged.TrySetResult();
				return ValueTask.CompletedTask;
			});
		await using var scopeRegistration = registration.ConfigureAwait(false);

		// Both sessions are live; close the FIRST one, then invalidate routing. Disposing the
		// session awaits its RunAsync, so a teardown fault surfaces here instead of being swallowed.
		await sessionA.DisposeAsync().ConfigureAwait(false);

		app.Map("late", () => "l");
		app.Core.InvalidateRouting();

		await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Guards snapshot isolation across sessions sharing one handler: a tool graph gated on session capabilities (module presence on IMcpClientRoots.IsSupported) must be computed per session — a shared snapshot cache would serve the roots-capable session's tools to a session without roots.")]
	public async Task When_ToolGraphIsSessionGated_Then_EachSessionSeesItsOwnTools()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.MapModule(new RootsGatedModule(), (IMcpClientRoots roots) => roots.IsSupported);
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();

		var withRoots = await StartSessionAsync(handler, BuildRootsClientOptions("file:///ga"), cts.Token).ConfigureAwait(false);
		await using var scopeWithRoots = withRoots.ConfigureAwait(false);
		var withoutRoots = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scopeWithoutRoots = withoutRoots.ConfigureAwait(false);

		var toolsWithRoots = await withRoots.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		var toolsWithoutRoots = await withoutRoots.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		toolsWithRoots.Should().Contain(tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal));
		toolsWithoutRoots.Should().Contain(tool => string.Equals(tool.Name, "always", StringComparison.Ordinal));
		toolsWithoutRoots.Should().NotContain(tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Locks the cache contract that makes a per-session tools/list legal on 2026-07-28: SEP-2549 permits list results to vary per client (CacheScope.Private is documented for \"filtered list results that vary per user\"), but an absent cacheScope defaults to Public, which would let a shared gateway serve one client's capability-gated catalog to another. A varying catalog must therefore be tagged private and immediately stale.")]
	public async Task When_ToolGraphIsSessionGated_Then_ListResultIsTaggedPrivateAndStale()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.MapModule(new RootsGatedModule(), (IMcpClientRoots roots) => roots.IsSupported);
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();

		var session = await StartSessionAsync(handler, BuildRootsClientOptions("file:///ga"), cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		session.Client.NegotiatedProtocolVersion.Should().Be(ModernProtocolVersion);
		var result = await session.Client.SendRequestAsync<ListToolsRequestParams, ListToolsResult>(
			RequestMethods.ToolsList,
			new ListToolsRequestParams(),
			cancellationToken: cts.Token).ConfigureAwait(false);

		result.Tools.Should().Contain(tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal));
		result.CacheScope.Should().Be(CacheScope.Private);
		result.TimeToLive.Should().Be(TimeSpan.Zero);
	}

	[TestMethod]
	[Description("Guards the compatibility-shim intro across sessions: DiscoverAndCallShim serves the discover_tools/call_tool intro on each session's FIRST tools/list — a handler-global flag would give the intro only to whichever session listed first, leaving later sessions without the documented bootstrap.")]
	public async Task When_ShimEnabledAndTwoSessionsList_Then_EachSessionGetsTheIntro()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("alpha", () => "a");
		var handler = CreateHandler(app, DynamicToolCompatibilityMode.DiscoverAndCallShim);
		using var cts = new CancellationTokenSource();

		var sessionA = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scopeA = sessionA.ConfigureAwait(false);
		var sessionB = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scopeB = sessionB.ConfigureAwait(false);

		var firstListA = await sessionA.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		var firstListB = await sessionB.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		firstListA.Select(static tool => tool.Name).Should().BeEquivalentTo(["discover_tools", "call_tool"]);
		firstListB.Select(static tool => tool.Name).Should().BeEquivalentTo(["discover_tools", "call_tool"]);
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

	private static Task<McpPipeSession> StartSessionAsync(
		McpServerHandler handler,
		McpClientOptions? clientOptions,
		CancellationToken cancellationToken) =>
		McpPipeSession.StartAsync(handler.RunAsync, clientOptions, cancellationToken);

	private static McpServerHandler CreateHandler(
		ReplApp app,
		DynamicToolCompatibilityMode compatibility = DynamicToolCompatibilityMode.Disabled)
	{
		var options = new ReplMcpServerOptions
		{
			DynamicToolCompatibility = compatibility,
			TransportFactory = McpTestFixture.PipeTransportFactory,
		};

		return new McpServerHandler(app.Core, options, McpTestFixture.EmptyServices);
	}

	private sealed class RootsGatedModule : IReplModule
	{
		public void Map(IReplMap app) => app.Map("gated", () => "roots-only");
	}

	// Roots is deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005) but still supported by
	// Repl.Mcp until the SDK removes the surface (#51).
#pragma warning disable MCP9005
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

	// Sampling carries the same SEP-2577 deprecation as Roots above.
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
#pragma warning restore MCP9005
}
