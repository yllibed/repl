using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpConcurrentSessions
{
	[TestMethod]
	[Description("Pins the protocol revision every other guarantee in this file is written against: two sessions sharing one handler must both negotiate 2026-07-28. Without this, a silent fallback to the 2025-11-25 initialize handshake would make the per-session capability and catalog assertions below describe a revision they were never meant to characterise.")]
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

		sessionA.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);
		sessionB.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);
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
	[Description("Guards routing-notification lifetime across sessions: when the first-attached session closes, the surviving session must still receive tools/list_changed after a routing invalidation — session attachment must be reference-counted, not first-wins with a handler-wide unsubscribe on first close. The surviving session subscribes through subscriptions/listen, which is how a 2026-07-28 client asks for the notification at all.")]
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

		using var listenCts = new CancellationTokenSource();
		var listenTask = sessionB.Client.SendRequestAsync<SubscriptionsListenRequestParams, EmptyResult>(
			RequestMethods.SubscriptionsListen,
			new SubscriptionsListenRequestParams
			{
				Notifications = new SubscriptionsListenNotifications { ToolsListChanged = true },
			},
			cancellationToken: listenCts.Token)
			.AsTask();

		// Both sessions are live; close the FIRST one, then invalidate routing. Disposing the
		// session awaits its RunAsync, so a teardown fault surfaces here instead of being swallowed.
		await sessionA.DisposeAsync().ConfigureAwait(false);

		app.Map("late", () => "l");
		app.Core.InvalidateRouting();

		await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		await listenCts.CancelAsync().ConfigureAwait(false);
		try
		{
			// Started above and awaited only after cancellation; MSTest has no sync context.
#pragma warning disable VSTHRD003
			await listenTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}
		catch (OperationCanceledException)
		{
			// Expected: the listen stream ends on cancellation.
		}
	}

	[TestMethod]
	[Description("Guards the other half of the 2026-07-28 rule: the advertised set MUST NOT change as a side effect of another request on the connection. A tool that writes session state and invalidates routing is exactly that side effect \u2014 it needs no second connection to be observable \u2014 so discovery must answer session state with a constant, the same way it already answers the capability services.")]
	public async Task When_AModernToolMutatesSessionState_Then_TheAdvertisedSetDoesNotChange()
	{
		var app = BuildSessionGatedApp();
		var handler = CreateHandlerWithAppServices(app);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var session = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);
		session.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var before = await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		await session.Client.CallToolAsync(
			toolName: "signin",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);

		var after = await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		after.Select(static tool => tool.Name).Should().BeEquivalentTo(
			before.Select(static tool => tool.Name),
			because: "a tools/call must not change what the next tools/list advertises on this revision");
		after.Should().NotContain(
			tool => string.Equals(tool.Name, "secret", StringComparison.Ordinal),
			because: "discovery reads a constant session state, so a gate on it decides once for everyone");
	}

	[TestMethod]
	[Description("The same shape on the initialize era, where the set is allowed to vary: a tool that writes session state and invalidates routing must still change what the next tools/list advertises. The freeze belongs to 2026-07-28 alone, and this guard is what stops it leaking into a revision whose whole dynamic-tools story depends on the graph moving.")]
	public async Task When_ALegacyToolMutatesSessionState_Then_TheAdvertisedSetChanges()
	{
		var app = BuildSessionGatedApp();
		var handler = CreateHandlerWithAppServices(app);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var session = await StartSessionAsync(
			handler,
			BuildLegacyRootsClientOptions("file:///ga"),
			cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);
		session.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.LastWithSessions);

		(await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Should().NotContain(tool => string.Equals(tool.Name, "secret", StringComparison.Ordinal));

		await session.Client.CallToolAsync(
			toolName: "signin",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);

		(await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Should().Contain(
				tool => string.Equals(tool.Name, "secret", StringComparison.Ordinal),
				because: "this revision has sessions, so a graph that moves with them is the point");
	}

	[TestMethod]
	[Description("Regression guard: what modern discovery advertises must be callable. Discovery answers the capability questions with constants, so a module gated on roots.IsSupported is offered to every client — and the documented contract is that calling it without the capability produces an actionable failure from the command itself. Execution resolved the graph again from the live services, so the route was simply absent and the caller got Unknown command instead, with nothing naming what was missing.")]
	public async Task When_AModernClientCallsACapabilityGatedTool_Then_TheCommandRuns()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.MapModule(new RootsGatedModule(), (IMcpClientRoots roots) => roots.IsSupported);
		var handler = CreateHandlerWithAppServices(app);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var session = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);
		session.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		(await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Should().Contain(tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal));

		var result = await session.Client.CallToolAsync(
			toolName: "gated",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);

		var text = string.Join(
			separator: '\n',
			values: result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

		text.Should().Contain(
			"roots-only",
			because: "the command was advertised to this client, so calling it must reach the handler");
		text.Should().NotContain(
			"unknown_command",
			because: "advertising a tool and then denying it exists tells the caller nothing it can act on");
	}

	[TestMethod]
	[Description("The counterpart: a command that discovery did NOT advertise must stay unreachable. Making the advertised set executable must not quietly open commands the graph hides — here the gate reads the roots DATA, which discovery answers as empty, so the command is offered to nobody and must be callable by nobody either.")]
	public async Task When_AModernClientCallsAnUnadvertisedGatedTool_Then_ItIsStillNotFound()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.MapModule(new RootsGatedModule(), (IMcpClientRoots roots) => roots.Current.Count > 0);
		var handler = CreateHandlerWithAppServices(app);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var session = await StartSessionAsync(
			handler,
			BuildRootsClientOptions("file:///ga"),
			cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		(await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Should().NotContain(tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal));

		var result = await session.Client.CallToolAsync(
			toolName: "gated",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);

		result.IsError.Should().BeTrue(
			because: "a command the catalog never offered must not become reachable by name");
	}

	[TestMethod]
	[Description("Regression guard: on 2026-07-28 a transient projection failure must not leave two connections serving different catalogs. Availability is worth preserving on both revisions, but a per-session fallback buys it with exactly the variance this revision forbids \u2014 a connection that had not yet seen a routing change would keep its older set while another served the newer one. Modern connections fall back to one shared last-known-good catalog instead, so they move together or not at all.")]
	public async Task When_AModernProjectionFailsTransiently_Then_EverySessionFallsBackTogether()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		var breakProjection = false;
		var handler = CreateHandlerWithAppServices(
			app,
			options => options.CommandFilter = _ => breakProjection
				? throw new InvalidOperationException("projection-failure")
				: true);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var older = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var olderScope = older.ConfigureAwait(false);

		// This connection reads the catalog before the change, and deliberately never reads it again
		// until the failure: it is the one that would otherwise be left behind.
		(await older.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Should().NotContain(tool => string.Equals(tool.Name, "added", StringComparison.Ordinal));

		app.Map("added", () => "new");
		app.Core.InvalidateRouting();

		var newer = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var newerScope = newer.ConfigureAwait(false);
		(await newer.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Should().Contain(tool => string.Equals(tool.Name, "added", StringComparison.Ordinal));

		breakProjection = true;
		app.Core.InvalidateRouting();

		var olderTools = await older.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		var newerTools = await newer.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		olderTools.Select(static tool => tool.Name).Should().BeEquivalentTo(
			newerTools.Select(static tool => tool.Name),
			because: "the set MUST NOT vary per connection, and a failure is not an exception to that");
	}

	/// <summary>An app whose module appears only once a command has written the session state.</summary>
	private static ReplApp BuildSessionGatedApp()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.Map("signin", (IReplSessionState state, ICoreReplApp core) =>
		{
			state.Set(key: SignedInKey, value: true);
			core.InvalidateRouting();
			return "ok";
		});
		app.MapModule(new SessionGatedModule(), (IReplSessionState state) => state.Get<bool>(SignedInKey));

		return app;
	}

	private const string SignedInKey = "auth.signed_in";

	/// <summary>
	/// A handler wired to the app's own container, which is what <c>mcp serve</c> does. The default
	/// helper passes an empty provider, so a command injecting a framework session service cannot bind.
	/// </summary>
	private static McpServerHandler CreateHandlerWithAppServices(
		ReplApp app,
		Action<ReplMcpServerOptions>? configure = null)
	{
		var options = new ReplMcpServerOptions { TransportFactory = McpTestFixture.PipeTransportFactory };
		configure?.Invoke(options);
		return new McpServerHandler(app.Core, options, app.Services);
	}

	private sealed class SessionGatedModule : IReplModule
	{
		public void Map(IReplMap app) => app.Map("secret", () => "classified").ReadOnly();
	}

	[TestMethod]
	[Description("Guards the 2026-07-28 rule that the advertised tool set MUST NOT vary per-connection: two sessions on one handler, one declaring roots and one not, must receive the SAME set. Discovery answers every per-connection question with a constant on that revision, so a module gated on IMcpClientRoots.IsSupported is advertised to both and fails with an actionable error if called where it cannot work.")]
	public async Task When_ModernSessionsShareAGatedGraph_Then_TheAdvertisedSetIsInvariant()
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

		withRoots.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);
		withoutRoots.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var toolsWithRoots = await withRoots.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		var toolsWithoutRoots = await withoutRoots.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		toolsWithoutRoots.Select(static tool => tool.Name).Should().BeEquivalentTo(
			toolsWithRoots.Select(static tool => tool.Name),
			because: "the set MUST NOT vary per-connection on this revision");
		toolsWithRoots.Should().Contain(tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal));
		toolsWithoutRoots.Should().Contain(tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Regression guard: listing tools on a modern revision must not send the client a roots/list. Discovery there consults no capability service, so pre-resolving roots before the build would reach the client as a side effect of tools/list for a result nothing reads — the interaction the invariance rule exists to prevent, and a round-trip on every rebuild.")]
	public async Task When_AModernSessionListsTools_Then_NoRootsRoundTripIsMade()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();
		var roundTrips = 0;

		var session = await StartSessionAsync(
			handler,
			BuildCountingRootsClientOptions(() => Interlocked.Increment(ref roundTrips)),
			cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		session.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		Volatile.Read(ref roundTrips).Should().Be(
			0,
			because: "nothing in a modern discovery pass reads the client's roots");
	}

	[TestMethod]
	[Description("Regression guard for the whole roots surface, not just its booleans: a predicate reading roots.Current — the workspace list itself — must not make the advertised set vary either. The discovery view reaches no live service at all, so every member answers a constant.")]
	public async Task When_AModernPredicateReadsTheRootsData_Then_TheAdvertisedSetIsStillInvariant()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.MapModule(new RootsGatedModule(), (IMcpClientRoots roots) => roots.Current.Count > 0);
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();

		var withRoots = await StartSessionAsync(handler, BuildRootsClientOptions("file:///ga"), cts.Token).ConfigureAwait(false);
		await using var scopeWithRoots = withRoots.ConfigureAwait(false);
		var withoutRoots = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scopeWithoutRoots = withoutRoots.ConfigureAwait(false);

		withRoots.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);
		withoutRoots.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var toolsWithRoots = await withRoots.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		var toolsWithoutRoots = await withoutRoots.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		toolsWithoutRoots.Select(static tool => tool.Name).Should().BeEquivalentTo(
			toolsWithRoots.Select(static tool => tool.Name),
			because: "reading the roots data must not vary the set any more than reading IsSupported does");

		// Equality alone is satisfied by two EMPTY sets, so state where a data-gated module actually
		// lands: discovery answers an empty root list, so the predicate is false for everyone.
		toolsWithRoots.Should().Contain(tool => string.Equals(tool.Name, "always", StringComparison.Ordinal));
		toolsWithRoots.Should().NotContain(
			tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal),
			because: "discovery answers an empty root list, so a predicate reading it is false for every "
				+ "client — the module is advertised to none, not to all");
	}

	[TestMethod]
	[Description("Regression guard pinning the whole discovery overlay, not one key of it: a client declaring NO capabilities must still be advertised every module gated on a capability being supported — roots, sampling, elicitation and feedback alike. Dropping any single service from the modern discovery overlay silently removes that module for everyone, and a test that only covers roots would stay green through it.")]
	public async Task When_AModernClientDeclaresNoCapabilities_Then_EveryCapabilityGatedModuleIsStillAdvertised()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.MapModule(new GateProbeModule("gate_roots"), (IMcpClientRoots roots) => roots.IsSupported);
		app.MapModule(new GateProbeModule("gate_sampling"), (IMcpSampling sampling) => sampling.IsSupported);
		app.MapModule(new GateProbeModule("gate_elicitation"), (IMcpElicitation elicitation) => elicitation.IsSupported);
		app.MapModule(new GateProbeModule("gate_feedback"), (IMcpFeedback feedback) => feedback.IsLoggingSupported);
		app.MapModule(new GateProbeModule("gate_progress"), (IMcpFeedback feedback) => feedback.IsProgressSupported);
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();

		var session = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		session.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var tools = (await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Select(static tool => tool.Name)
			.ToArray();

		tools.Should().Contain("always");
		foreach (var gated in new[]
			{
				"gate_roots", "gate_sampling", "gate_elicitation", "gate_feedback", "gate_progress",
			})
		{
			tools.Should().Contain(
				gated,
				because: "this client declares nothing, so {0} proves its capability service is neutralised in discovery",
				gated);
		}
	}

	[TestMethod]
	[Description("Guards that the invariance above is scoped to the revision that requires it: the initialize-era revisions establish a session and state no such rule, so a capability-gated graph is still computed per session there. Without this, making the modern set invariant could silently take the feature away from every legacy client too.")]
	public async Task When_LegacySessionsShareAGatedGraph_Then_EachSeesItsOwnTools()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.MapModule(new RootsGatedModule(), (IMcpClientRoots roots) => roots.IsSupported);
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();

		var withRoots = await StartSessionAsync(handler, BuildLegacyRootsClientOptions("file:///ga"), cts.Token).ConfigureAwait(false);
		await using var scopeWithRoots = withRoots.ConfigureAwait(false);
		var withoutRoots = await StartSessionAsync(
			handler,
			new McpClientOptions { ProtocolVersion = McpProtocolRevisions.LastWithSessions },
			cts.Token).ConfigureAwait(false);
		await using var scopeWithoutRoots = withoutRoots.ConfigureAwait(false);

		withRoots.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.LastWithSessions);
		withoutRoots.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.LastWithSessions);

		var toolsWithRoots = await withRoots.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		var toolsWithoutRoots = await withoutRoots.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		toolsWithRoots.Should().Contain(tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal));
		toolsWithoutRoots.Should().Contain(tool => string.Equals(tool.Name, "always", StringComparison.Ordinal));
		toolsWithoutRoots.Should().NotContain(tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Pins Repl's own cache tagging on 2026-07-28 rather than whatever the SDK defaults to: SEP-2549 reads an absent cacheScope as Public, which would let a shared gateway hand Repl's list to the next caller. The tag is a conservative default, NOT what makes a varying list legal — that rule is satisfied by the set being invariant, guarded separately. Every list the handler serves is tagged private and immediately stale.")]
	public async Task When_ModernClientListsTools_Then_ListResultIsTaggedPrivateAndStale()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.MapModule(new RootsGatedModule(), (IMcpClientRoots roots) => roots.IsSupported);
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource();

		var session = await StartSessionAsync(handler, BuildRootsClientOptions("file:///ga"), cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		session.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);
		var result = await session.Client.SendRequestAsync<ListToolsRequestParams, ListToolsResult>(
			RequestMethods.ToolsList,
			new ListToolsRequestParams(),
			cancellationToken: cts.Token).ConfigureAwait(false);

		result.Tools.Should().Contain(tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal));
		result.CacheScope.Should().Be(CacheScope.Private);
		result.TimeToLive.Should().Be(TimeSpan.Zero);
	}

	[TestMethod]
	[Description("Guards the compatibility-shim intro across sessions: DiscoverAndCallShim serves the discover_tools/call_tool intro on each session's FIRST tools/list — a handler-global flag would give the intro only to whichever session listed first, leaving later sessions without the documented bootstrap. Pinned to an initialize-era revision, which is where a catalog may change across requests on one connection.")]
	public async Task When_ShimEnabledAndTwoLegacySessionsList_Then_EachSessionGetsTheIntro()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("alpha", () => "a");
		var handler = CreateHandler(app, DynamicToolCompatibilityMode.DiscoverAndCallShim);
		using var cts = new CancellationTokenSource();

		var legacy = new McpClientOptions { ProtocolVersion = McpProtocolRevisions.LastWithSessions };
		var sessionA = await StartSessionAsync(handler, legacy, cts.Token).ConfigureAwait(false);
		await using var scopeA = sessionA.ConfigureAwait(false);
		var sessionB = await StartSessionAsync(handler, legacy, cts.Token).ConfigureAwait(false);
		await using var scopeB = sessionB.ConfigureAwait(false);

		sessionA.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.LastWithSessions);

		var firstListA = await sessionA.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		var firstListB = await sessionB.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		firstListA.Select(static tool => tool.Name).Should().BeEquivalentTo(["discover_tools", "call_tool"]);
		firstListB.Select(static tool => tool.Name).Should().BeEquivalentTo(["discover_tools", "call_tool"]);
	}

	[TestMethod]
	[Description("Regression guard: the compatibility bootstrap must not run on 2026-07-28. Serving discover_tools/call_tool on the first tools/list and the real catalog on the next is a connection-local change caused by another request on that connection — the second half of the MUST NOT, and observable with a single connection. A modern client gets the real catalog immediately, and twice in a row it gets the same one.")]
	public async Task When_ShimEnabledAndAModernSessionLists_Then_TheCatalogIsTheSameEveryTime()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("alpha", () => "a");
		var handler = CreateHandler(app, DynamicToolCompatibilityMode.DiscoverAndCallShim);
		using var cts = new CancellationTokenSource();

		var session = await StartSessionAsync(handler, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		session.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var firstList = await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		var secondList = await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		firstList.Select(static tool => tool.Name).Should().BeEquivalentTo(
			["alpha"],
			because: "the bootstrap pair would be a catalog this connection only sees once");
		secondList.Select(static tool => tool.Name).Should().BeEquivalentTo(
			firstList.Select(static tool => tool.Name),
			because: "the set must not change as a side effect of the previous request");
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
	[TestMethod]
	[Description("Regression guard: one connection really can be served both eras, so the snapshot cache key has to carry the era. The SDK accepts a modern per-request _meta call and then an initialize handshake on the same pipe, and the two eras see different command graphs — a capability gate reads as supported during modern discovery and against the real client on legacy. Without the era in the key the second request is served the first one\u0027s catalog.")]
	public async Task When_OneConnectionIsServedBothEras_Then_EachGetsItsOwnCatalog()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.MapModule(new RootsGatedModule(), (IMcpClientRoots roots) => roots.IsSupported);
		var handler = CreateHandler(app);

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var clientToServer = new Pipe();
		var serverToClient = new Pipe();
		var io = new McpRawIo(clientToServer, serverToClient);
		var serverTask = handler.RunAsync(
			new McpTestFixture.PipeIoContext(
				clientToServer.Reader.AsStream(),
				serverToClient.Writer.AsStream()),
			cts.Token);

		try
		{
			// A modern request: the era travels in _meta, there is no handshake.
			var modern = await io.CallAsync(
				id: 1,
				method: "tools/list",
				meta: new JsonObject
				{
					["io.modelcontextprotocol/protocolVersion"] = McpProtocolRevisions.Sessionless,
					["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
				},
				cancellationToken: cts.Token).ConfigureAwait(false);

			ToolNames(modern).Should().Contain(
				"gated",
				because: "modern discovery answers IsSupported with a constant, so the gate matches for everyone");

			// The same connection then opens a legacy session, which the SDK accepts.
			await io.InitializeLegacyAsync(id: 2, cts.Token).ConfigureAwait(false);

			var legacy = await io.CallAsync(
				id: 3,
				method: "tools/list",
				meta: null,
				cancellationToken: cts.Token).ConfigureAwait(false);

			ToolNames(legacy).Should().Contain("always");
			ToolNames(legacy).Should().NotContain(
				"gated",
				because: "this legacy client declares no roots, so it must not be served the catalog the "
					+ "modern request seeded on the same connection");
		}
		finally
		{
			await StopRawServerAsync(cts, io, serverTask).ConfigureAwait(false);
		}
	}

	private static async Task StopRawServerAsync(CancellationTokenSource cts, McpRawIo io, Task serverTask)
	{
		await cts.CancelAsync().ConfigureAwait(false);
		await io.DisposeAsync().ConfigureAwait(false);
		try
		{
			await serverTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Expected: RunAsync ends on cancellation.
		}
	}

	private static IReadOnlyList<string> ToolNames(JsonObject response) =>
		[
			.. (response["result"]?["tools"]?.AsArray() ?? [])
				.Select(static tool => tool?["name"]?.GetValue<string>() ?? ""),
		];

	/// <summary>
	/// A raw newline-delimited JSON-RPC peer. <see cref="McpClient"/> cannot express this test: it
	/// speaks one era for the life of a connection, and the point here is to send both down one pipe.
	/// </summary>
	private sealed class McpRawIo(Pipe clientToServer, Pipe serverToClient) : IAsyncDisposable
	{
		private readonly StreamReader _reader = new(serverToClient.Reader.AsStream(), Encoding.UTF8);

		public async Task<JsonObject> CallAsync(
			int id,
			string method,
			JsonObject? meta,
			CancellationToken cancellationToken,
			JsonObject? parameters = null)
		{
			var parameterObject = parameters ?? [];
			if (meta is not null)
			{
				parameterObject["_meta"] = meta;
			}

			await WriteAsync(
				new JsonObject
				{
					["jsonrpc"] = "2.0",
					["id"] = id,
					["method"] = method,
					["params"] = parameterObject,
				},
				cancellationToken).ConfigureAwait(false);

			while (await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
			{
				if (JsonNode.Parse(line) is not JsonObject frame)
				{
					continue;
				}

				if (frame["id"] is JsonValue value && value.TryGetValue(out int responseId) && responseId == id)
				{
					return frame;
				}
			}

			throw new InvalidOperationException($"No response for request {id}.");
		}

		/// <summary>Opens an initialize-era session on this same connection.</summary>
		public async Task InitializeLegacyAsync(int id, CancellationToken cancellationToken)
		{
			await CallAsync(
				id,
				"initialize",
				meta: null,
				cancellationToken,
				parameters: new JsonObject
				{
					["protocolVersion"] = McpProtocolRevisions.LastWithSessions,
					["capabilities"] = new JsonObject(),
					["clientInfo"] = new JsonObject
					{
						["name"] = "mixed-era-probe",
						["version"] = "1.0.0",
					},
				}).ConfigureAwait(false);

			await NotifyAsync("notifications/initialized", cancellationToken).ConfigureAwait(false);
		}

		public Task NotifyAsync(string method, CancellationToken cancellationToken) =>
			WriteAsync(
				new JsonObject
				{
					["jsonrpc"] = "2.0",
					["method"] = method,
					["params"] = new JsonObject(),
				},
				cancellationToken);

		public async ValueTask DisposeAsync()
		{
			_reader.Dispose();
			await clientToServer.Writer.CompleteAsync().ConfigureAwait(false);
			await serverToClient.Writer.CompleteAsync().ConfigureAwait(false);
		}

		private async Task WriteAsync(JsonObject frame, CancellationToken cancellationToken)
		{
			var payload = Encoding.UTF8.GetBytes(frame.ToJsonString() + "\n");
			await clientToServer.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
			await clientToServer.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	[TestMethod]
	[Description("Regression guard: two first invocations arriving together on one connection must share a single roots/list. Execution-boundary priming runs on every call, so without a shared in-flight fetch a burst of concurrent calls multiplies reverse requests to the client and contradicts the one-round-trip-per-connection cost this design claims. The second call is issued only once the first roots/list is known to be outstanding and unanswered, because two calls merely started together can still run one after the other \u2014 and a second call that reads a cache the first already filled pays one round-trip whether or not anything is shared.")]
	public async Task When_TwoFirstCallsRaceOnOneConnection_Then_TheyShareOneRootsRoundTrip()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("peek", (IMcpClientRoots roots) => string.Join(',', roots.Current.Select(static r => r.Uri.ToString())));
		var handler = CreateHandler(app);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var roundTrips = 0;
		var firstRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var session = await StartSessionAsync(
			handler,
			BuildParkedRootsClientOptions(
				() => Interlocked.Increment(ref roundTrips),
				firstRequested,
				release.Task),
			cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		var first = session.Client.CallToolAsync(
			"peek",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token);

		// Only now is the first fetch certainly in flight and certainly unanswered, which is the state a
		// second caller has to arrive in for there to be anything to share.
		await firstRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(false);

		var second = session.Client.CallToolAsync(
			"peek",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token);

		// A second prime that did not join the outstanding fetch has to ask the client itself, and the
		// counter moves when that ask is received rather than when it is answered \u2014 so an unshared fetch
		// shows up here even though every answer is still held.
		await Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);
		Volatile.Read(ref roundTrips).Should().Be(
			1,
			because: "the second caller must join the fetch already in flight rather than start its own");

		release.SetResult();
		var results = await Task.WhenAll(first.AsTask(), second.AsTask()).ConfigureAwait(false);

		foreach (var result in results)
		{
			result.Content.OfType<TextContentBlock>().First().Text.Should().Contain("file:///ga");
		}

		Volatile.Read(ref roundTrips).Should().Be(
			1,
			because: "the connection-scoped fetch is shared, so a race pays one roots/list, not one each");
	}

	[TestMethod]
	[Description("Regression guard: roots/list_changed must retire the shared in-flight task, not only the cached array. That task exists to coalesce concurrent first calls, so a COMPLETED one left behind after an invalidation makes the next execution replay the pre-notification answer and send no roots/list at all \u2014 the cache is cleared and nothing refills it.")]
	public async Task When_RootsListChangedFollowsACompletedFetch_Then_TheNextCallRefetches()
	{
		var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var roundTrips = 0;
		var session = await StartRootsVersionSessionAsync(() => Interlocked.Increment(ref roundTrips)).ConfigureAwait(false);
		await using var scope = session.Session.ConfigureAwait(false);

		await using var registration = session.Session.Client.RegisterNotificationHandler(
			NotificationMethods.ToolListChangedNotification,
			(_, _) =>
			{
				listChanged.TrySetResult();
				return ValueTask.CompletedTask;
			}).ConfigureAwait(false);

		(await PeekRootsAsync(session, session.Cts.Token).ConfigureAwait(false)).Should().Contain("workspace-v1");

		await session.Session.Client.SendNotificationAsync(
			NotificationMethods.RootsListChangedNotification,
			cancellationToken: session.Cts.Token).ConfigureAwait(false);
		await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		(await PeekRootsAsync(session, session.Cts.Token).ConfigureAwait(false)).Should().Contain(
			"workspace-v2",
			because: "the invalidation must send the client back to the wire, not replay its previous answer");
		Volatile.Read(ref roundTrips).Should().Be(2);
	}

	[TestMethod]
	[Description("Regression guard: a roots/list answered after the client has invalidated its roots must be refetched rather than cached or reported. A fetch carries the version it started under, and a roots/list_changed processed while it is still unanswered makes that version stale \u2014 caching the late answer would pin roots the client already retracted, and handing it back would let the command run against roots it has already replaced. The notification is ordered against the unanswered fetch by holding the roots/list answer until the server echoes tools/list_changed, which it only emits once the invalidation has been applied.")]
	public async Task When_RootsAreInvalidatedWhileTheFetchIsUnanswered_Then_TheLateAnswerIsNotCached()
	{
		var roundTrips = 0;
		var firstRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("peek", (IMcpClientRoots roots) => string.Join(',', roots.Current.Select(static r => r.Uri.ToString())));
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var session = await StartSessionAsync(
			CreateHandler(app),
			BuildParkedVersionedRootsClientOptions(
				() => Interlocked.Increment(ref roundTrips),
				firstRequested,
				release.Task),
			cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		await using var registration = session.Client.RegisterNotificationHandler(
			NotificationMethods.ToolListChangedNotification,
			(_, _) =>
			{
				listChanged.TrySetResult();
				return ValueTask.CompletedTask;
			}).ConfigureAwait(false);

		var parked = session.Client.CallToolAsync(
			"peek",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token);
		await firstRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(false);

		await session.Client.SendNotificationAsync(
			NotificationMethods.RootsListChangedNotification,
			cancellationToken: cts.Token).ConfigureAwait(false);

		// The echo is the ordering: the server emits it from the same handler that bumps the version, so
		// receiving it proves the version moved while the fetch above is still unanswered.
		await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(false);

		release.SetResult();
		var parkedResult = await parked.AsTask().ConfigureAwait(false);

		parkedResult.Content.OfType<TextContentBlock>().First().Text.Should().Contain(
			"workspace-v2",
			because: "the call whose answer was retired asks again, so its command still gets a boundary");

		var result = await session.Client.CallToolAsync(
			"peek",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);

		result.Content.OfType<TextContentBlock>().First().Text.Should().Contain(
			"workspace-v2",
			because: "an answer that arrived after its version was retired must not be what the next caller reads");
		Volatile.Read(ref roundTrips).Should().Be(
			2,
			because: "the retired answer left nothing cached, so the next caller goes back to the client");
	}

	[TestMethod]
	[Description("Regression guard: a roots fetch that ends badly must be retracted, so the next caller reaches the client again instead of inheriting the failure. Without it a single unusable answer latches for the life of the connection, and the caller that would have retried is the one that never learns there was anything to retry.")]
	public async Task When_AConnectionScopedFetchFails_Then_TheNextCallerReachesTheClientAgain()
	{
		var roundTrips = 0;
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("resolve", async (IMcpClientRoots roots, CancellationToken ct) =>
			string.Join(',', (await roots.GetAsync(ct).ConfigureAwait(false)).Select(static r => r.Uri.ToString())));
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var session = await StartSessionAsync(
			CreateHandler(app),
			BuildFirstAnswerUnusableRootsClientOptions(() => Interlocked.Increment(ref roundTrips)),
			cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		// The execution prologue absorbs the unusable first answer and swallows it; the handler's own
		// GetAsync is the next caller, and must not inherit that failure.
		var result = await session.Client.CallToolAsync(
			toolName: "resolve",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);

		result.Content.OfType<TextContentBlock>().First().Text.Should().Contain(
			"file:///recovered",
			because: "the failed fetch must be retracted, not handed to everyone who asks next");
		Volatile.Read(ref roundTrips).Should().Be(2);
	}

	[TestMethod]
	[Description("Regression guard: when a roots-capable client cannot be resolved, Current must fall back to the soft roots rather than report an empty set. The eager prime absorbs the failure so that commands which never read roots still run, which leaves Current answering for a resolution that never happened \u2014 and an empty answer reads as \u0027this client declared no roots\u0027, which is the reading a handler acts on and the one it cannot check. A client that genuinely answers with zero roots is still told apart, because that answer is recorded as resolved.")]
	public async Task When_TheNativeFetchFails_Then_CurrentFallsBackToSoftRoots()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("peek", (IMcpClientRoots roots) =>
		{
			// The prime has already run and failed by the time a handler body executes.
			roots.SetSoftRoots([new McpClientRoot(new Uri("file:///soft", UriKind.Absolute), "soft")]);
			return string.Join(',', roots.Current.Select(static r => r.Uri.ToString()));
		});
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var session = await StartSessionAsync(
			CreateHandler(app),
			BuildUnusableRootsClientOptions(),
			cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		var result = await session.Client.CallToolAsync(
			toolName: "peek",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);

		result.Content.OfType<TextContentBlock>().First().Text.Should().Contain(
			"file:///soft",
			because: "nothing native was resolved, so the roots actually in force are the soft ones");
	}

	[TestMethod]
	[Description("Regression guard: a direct GetAsync waiter must never be handed an answer that was retired while it was in flight. Refusing to cache such an answer is not enough \u2014 returning it gives the caller roots the client has already retracted, with nothing to tell it apart from a current one. The command boundary masks this, because a later prime refetches before the handler reads Current; only the value GetAsync itself returns shows it.")]
	public async Task When_AnInFlightFetchIsRetired_Then_TheDirectWaiterIsNotHandedIt()
	{
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var echo = new SemaphoreSlim(0);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var session = await StartSessionAsync(
			CreateHandler(BuildDirectWaiterApp(entered, proceed.Task)),
			BuildParkOnSecondRootsClientOptions(secondStarted, release.Task),
			cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		await using var registration = session.Client.RegisterNotificationHandler(
			NotificationMethods.ToolListChangedNotification,
			(_, _) =>
			{
				echo.Release();
				return ValueTask.CompletedTask;
			}).ConfigureAwait(false);

		var call = session.Client.CallToolAsync(
			toolName: "resolve",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token);

		// The prime has resolved and cached workspace-v1, and the handler is parked before its own read.
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(false);
		await InvalidateAndAwaitEchoAsync(session, echo, cts.Token).ConfigureAwait(false);

		// Now the handler asks for itself, and that fetch is the one held unanswered.
		proceed.SetResult();
		await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(false);
		await InvalidateAndAwaitEchoAsync(session, echo, cts.Token).ConfigureAwait(false);

		release.SetResult();
		var result = await call.AsTask().ConfigureAwait(false);

		result.Content.OfType<TextContentBlock>().First().Text.Should().Contain(
			"workspace-v3",
			because: "the answer that arrived after its version was retired must not be what the caller receives");
	}

	private static ReplApp BuildDirectWaiterApp(TaskCompletionSource entered, Task proceed)
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("resolve", async (IMcpClientRoots roots, CancellationToken ct) =>
		{
			entered.TrySetResult();
#pragma warning disable VSTHRD003 // A gate owned by the test, completed by the test.
			await proceed.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			var resolved = await roots.GetAsync(ct).ConfigureAwait(false);
			return string.Join(',', resolved.Select(static r => r.Uri.ToString()));
		});

		return app;
	}

	private static async Task InvalidateAndAwaitEchoAsync(
		McpPipeSession session,
		SemaphoreSlim echo,
		CancellationToken cancellationToken)
	{
		await session.Client.SendNotificationAsync(
			NotificationMethods.RootsListChangedNotification,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		// The server emits tools/list_changed from the same handler that moves the roots version, so the
		// echo proves the invalidation has been applied rather than merely sent.
		await echo.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>A client that holds only its second <c>roots/list</c>, and versions every answer.</summary>
	private static McpClientOptions BuildParkOnSecondRootsClientOptions(
		TaskCompletionSource secondStarted,
		Task release)
	{
		var calls = 0;
		var options = BuildRootsClientOptions("file:///unused");
		// roots/list_changed was removed from 2026-07-28 by SEP-2575, so this guard belongs to the
		// initialize era. The defect it pins is in the roots service and is era-independent.
		options.ProtocolVersion = McpProtocolRevisions.LastWithSessions;
		options.Handlers = new McpClientHandlers
		{
			RootsHandler = async (_, _) =>
			{
				var n = Interlocked.Increment(ref calls);
				if (n == 2)
				{
					secondStarted.TrySetResult();
#pragma warning disable VSTHRD003 // A gate owned by the test, completed by the test.
					await release.ConfigureAwait(false);
#pragma warning restore VSTHRD003
				}

				var uri = "file:///workspace-v" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
				return new ListRootsResult
				{
					Roots = [new Root { Uri = uri, Name = "workspace" }],
				};
			},
		};

		return options;
	}

	/// <summary>A roots-capable client whose every answer fails while being mapped.</summary>
	private static McpClientOptions BuildUnusableRootsClientOptions()
	{
		var options = BuildRootsClientOptions("file:///unused");
		options.Handlers = new McpClientHandlers
		{
			// Fails server-side while being mapped, which is a real fetch failure and needs no
			// client-side throw \u2014 a handler that throws escapes the SDK's own message loop.
			RootsHandler = static (_, _) => ValueTask.FromResult(new ListRootsResult
			{
				Roots = [new Root { Uri = "http://", Name = "workspace" }],
			}),
		};

		return options;
	}

	/// <summary>A client whose first <c>roots/list</c> answer cannot be mapped, and whose next can.</summary>
	private static McpClientOptions BuildFirstAnswerUnusableRootsClientOptions(Action onRequest)
	{
		var calls = 0;
		var options = BuildRootsClientOptions("file:///unused");
		options.Handlers = new McpClientHandlers
		{
			// The first answer fails server-side while being mapped, which is a real fetch failure and
			// needs no client-side throw — a handler that throws escapes the SDK's own message loop.
			RootsHandler = (_, _) =>
			{
				onRequest();
				var uri = Interlocked.Increment(ref calls) == 1 ? "http://" : "file:///recovered";
				return ValueTask.FromResult(new ListRootsResult
				{
					Roots = [new Root { Uri = uri, Name = "workspace" }],
				});
			},
		};

		return options;
	}

	private static async Task<string> PeekRootsAsync(RootsVersionSession session, CancellationToken cancellationToken)
	{
		var result = await session.Session.Client.CallToolAsync(
			toolName: "peek",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cancellationToken).ConfigureAwait(false);

		return result.Content.OfType<TextContentBlock>().First().Text;
	}

	private sealed record RootsVersionSession(McpPipeSession Session, CancellationTokenSource Cts);

	private static async Task<RootsVersionSession> StartRootsVersionSessionAsync(Action onRequest)
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("peek", (IMcpClientRoots roots) => string.Join(',', roots.Current.Select(static r => r.Uri.ToString())));
		var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var session = await StartSessionAsync(
			CreateHandler(app),
			BuildVersionedRootsClientOptions(onRequest),
			cts.Token).ConfigureAwait(false);

		return new RootsVersionSession(session, cts);
	}

	private sealed class GateProbeModule(string commandName) : IReplModule
	{
		public void Map(IReplMap app) => app.Map(commandName, () => "ok").ReadOnly();
	}

	/// <summary>A client whose every <c>roots/list</c> answer names a new workspace version.</summary>
	private static McpClientOptions BuildVersionedRootsClientOptions(Action onRequest)
	{
		var version = 0;
		var options = BuildRootsClientOptions("file:///unused");
		// roots/list_changed was removed from 2026-07-28 by SEP-2575, and that revision delivers no
		// unsolicited list_changed either, so this guard belongs to the initialize era. The defect it
		// pins is in the roots service and is era-independent.
		options.ProtocolVersion = McpProtocolRevisions.LastWithSessions;
		options.Handlers = new McpClientHandlers
		{
			RootsHandler = (_, _) =>
			{
				onRequest();
				var uri = "file:///workspace-v" + Interlocked.Increment(ref version).ToString(
					System.Globalization.CultureInfo.InvariantCulture);
				return ValueTask.FromResult(new ListRootsResult
				{
					Roots = [new Root { Uri = uri, Name = "workspace" }],
				});
			},
		};

		return options;
	}

	/// <summary>
	/// A client that holds its first <c>roots/list</c> answer until told to let go, so a test can work
	/// with a fetch that is certainly outstanding and certainly unanswered.
	/// </summary>
	/// <remarks>
	/// The count is taken on arrival rather than on the answer: a second fetch has to be visible while
	/// every answer is still held, which is the whole point of holding them.
	/// </remarks>
	private static McpClientOptions BuildParkedRootsClientOptions(
		Action onRequest,
		TaskCompletionSource firstRequested,
		Task release)
	{
		var options = BuildRootsClientOptions("file:///ga");
		options.Handlers = new McpClientHandlers
		{
			RootsHandler = async (_, _) =>
			{
				onRequest();
				firstRequested.TrySetResult();
#pragma warning disable VSTHRD003 // A gate owned by the test, completed by the test.
				await release.ConfigureAwait(false);
#pragma warning restore VSTHRD003
				return new ListRootsResult
				{
					Roots = [new Root { Uri = "file:///ga", Name = "ga" }],
				};
			},
		};

		return options;
	}

	/// <summary>
	/// <see cref="BuildParkedRootsClientOptions"/> with a workspace version per answer, so a caller can
	/// be told apart by which answer it read.
	/// </summary>
	private static McpClientOptions BuildParkedVersionedRootsClientOptions(
		Action onRequest,
		TaskCompletionSource firstRequested,
		Task release)
	{
		var version = 0;
		var options = BuildRootsClientOptions("file:///unused");
		// roots/list_changed was removed from 2026-07-28 by SEP-2575, so this guard belongs to the
		// initialize era. The defect it pins is in the roots service and is era-independent.
		options.ProtocolVersion = McpProtocolRevisions.LastWithSessions;
		options.Handlers = new McpClientHandlers
		{
			RootsHandler = async (_, _) =>
			{
				onRequest();
				var uri = "file:///workspace-v" + Interlocked.Increment(ref version).ToString(
					System.Globalization.CultureInfo.InvariantCulture);
				if (firstRequested.TrySetResult())
				{
#pragma warning disable VSTHRD003 // A gate owned by the test, completed by the test.
					await release.ConfigureAwait(false);
#pragma warning restore VSTHRD003
				}

				return new ListRootsResult
				{
					Roots = [new Root { Uri = uri, Name = "workspace" }],
				};
			},
		};

		return options;
	}

	private static McpClientOptions BuildCountingRootsClientOptions(Action onRequest)
	{
		var options = BuildRootsClientOptions("file:///ga");
		options.Handlers = new McpClientHandlers
		{
			RootsHandler = (_, _) =>
			{
				onRequest();
				return ValueTask.FromResult(new ListRootsResult
				{
					Roots = [new Root { Uri = "file:///ga", Name = "ga" }],
				});
			},
		};
		return options;
	}

	private static McpClientOptions BuildLegacyRootsClientOptions(string rootUri)
	{
		var options = BuildRootsClientOptions(rootUri);
		options.ProtocolVersion = McpProtocolRevisions.LastWithSessions;
		return options;
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
