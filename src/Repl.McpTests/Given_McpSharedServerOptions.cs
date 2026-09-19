using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// Regressions for the documented multi-connection host pattern: build
/// <c>BuildMcpServerOptions()</c> ONCE and create an <see cref="McpServer"/> per connection
/// (docs/mcp-transports.md). That path bypasses <see cref="McpServerHandler"/>'s request handlers
/// entirely — the SDK dispatches straight into the pre-built primitives — so nothing that relies on
/// the handler prologue applies to it.
/// </summary>
[TestClass]
public sealed class Given_McpSharedServerOptions
{
	[TestMethod]
	[Description("Guards capability resolution on the documented reusable-options path: two connections created from ONE BuildMcpServerOptions() result must each observe their OWN client's capabilities. The pre-built primitives never run the handler's request prologue, so without per-invocation request binding a sampling-capable client is told sampling is unavailable — the capability is resolved against nothing at all.")]
	public async Task When_TwoConnectionsShareOneOptionsInstance_Then_CapabilitiesAreRequestScoped()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		// Distinct tokens, not "supported"/"not-supported": the tool result is JSON, so the text block
		// carries quotes, and a substring assertion on the shorter word would match both answers.
		app.Map("probe", (IMcpSampling sampling) => sampling.IsSupported ? "sampling-on" : "sampling-off");

		// Built once and reused across connections, exactly as docs/mcp-transports.md prescribes.
		var mcpOptions = app.BuildMcpServerOptions();
		using var cts = new CancellationTokenSource();

		var capable = await StartAsync(mcpOptions, BuildSamplingClientOptions(), cts.Token).ConfigureAwait(false);
		await using var capableScope = capable.ConfigureAwait(false);
		var plain = await StartAsync(mcpOptions, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var plainScope = plain.ConfigureAwait(false);

		capable.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);
		plain.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var capableText = await CallProbeAsync(capable, cts.Token).ConfigureAwait(false);
		var plainText = await CallProbeAsync(plain, cts.Token).ConfigureAwait(false);

		capableText.Should().Contain("sampling-on");
		plainText.Should().Contain("sampling-off");
	}

	[TestMethod]
	[Description("Guards native-root isolation on the documented reusable-options path: two connections created from ONE BuildMcpServerOptions() result must each observe their OWN client's workspace roots. The pre-built primitives share a single McpClientRootsService, so a cache keyed to that instance hands the second client the first client's filesystem URIs without ever asking it — a cross-client disclosure, not merely a stale read.")]
	public async Task When_TwoRootCapableClientsShareOneOptionsInstance_Then_EachSeesOwnRoots()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("roots", async (IMcpClientRoots roots, CancellationToken ct) =>
			string.Join(',', (await roots.GetAsync(ct).ConfigureAwait(false)).Select(root => root.Uri.ToString())));

		var mcpOptions = app.BuildMcpServerOptions();
		using var cts = new CancellationTokenSource();

		var first = await StartAsync(mcpOptions, BuildRootsClientOptions("file:///ga"), cts.Token).ConfigureAwait(false);
		await using var firstScope = first.ConfigureAwait(false);
		var second = await StartAsync(mcpOptions, BuildRootsClientOptions("file:///bu"), cts.Token).ConfigureAwait(false);
		await using var secondScope = second.ConfigureAwait(false);

		var firstText = await CallAsync(first, "roots", cts.Token).ConfigureAwait(false);
		var secondText = await CallAsync(second, "roots", cts.Token).ConfigureAwait(false);

		firstText.Should().Contain("file:///ga");
		secondText.Should().Contain("file:///bu");
		secondText.Should().NotContain("file:///ga", because: "one client's workspace must never reach another");
	}

	[TestMethod]
	[Description("Guards the other read the shared cache leaks through: Current returns the cached hard roots whenever the flowing request declares the roots capability, so a client that never asked for roots itself still receives whatever the previous connection's roots/list returned. Reading Current is the documented cheap path for a command that does not want a round-trip, which is exactly why it must not answer with someone else's workspace.")]
	public async Task When_ASecondClientReadsCurrentRoots_Then_ItDoesNotSeeTheFirstClientsRoots()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("fetch", async (IMcpClientRoots roots, CancellationToken ct) =>
			string.Join(',', (await roots.GetAsync(ct).ConfigureAwait(false)).Select(root => root.Uri.ToString())));
		app.Map("peek", (IMcpClientRoots roots) =>
			$"[{string.Join(',', roots.Current.Select(root => root.Uri.ToString()))}]");
		app.Map("fetch-then-peek", async (IMcpClientRoots roots, CancellationToken ct) =>
		{
			await roots.GetAsync(ct).ConfigureAwait(false);
			return $"[{string.Join(',', roots.Current.Select(root => root.Uri.ToString()))}]";
		});

		var mcpOptions = app.BuildMcpServerOptions();
		using var cts = new CancellationTokenSource();

		var first = await StartAsync(mcpOptions, BuildRootsClientOptions("file:///ga"), cts.Token).ConfigureAwait(false);
		await using var firstScope = first.ConfigureAwait(false);
		var second = await StartAsync(mcpOptions, BuildRootsClientOptions("file:///bu"), cts.Token).ConfigureAwait(false);
		await using var secondScope = second.ConfigureAwait(false);

		// The first connection populates whatever cache exists; the second only ever peeks.
		(await CallAsync(first, "fetch", cts.Token).ConfigureAwait(false)).Should().Contain("file:///ga");

		var peeked = await CallAsync(second, "peek", cts.Token).ConfigureAwait(false);

		peeked.Should().NotContain("file:///ga");

		// The other half: once this request has resolved, Current must answer with what it resolved.
		// Without this, removing the resolved-value path entirely would keep the assertion above green
		// while leaving Current permanently empty.
		var fetched = await CallAsync(second, "fetch-then-peek", cts.Token).ConfigureAwait(false);

		fetched.Should().Contain("file:///bu");
		fetched.Should().NotContain("file:///ga");
	}

	[TestMethod]
	[Description("Pins the cost of the isolation above: resolving roots twice inside ONE tool invocation must still cost a single roots/list round-trip. Isolating per request rather than per connection is only correct if it memoises within the request — otherwise every IMcpClientRoots call becomes a client round-trip, which would trade a disclosure for a latency regression.")]
	public async Task When_OneRequestResolvesRootsTwice_Then_OnlyOneRootsListRoundTrip()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("twice", async (IMcpClientRoots roots, CancellationToken ct) =>
		{
			var once = await roots.GetAsync(ct).ConfigureAwait(false);
			var twice = await roots.GetAsync(ct).ConfigureAwait(false);
			return $"{once.Count}|{twice.Count}";
		});

		var mcpOptions = app.BuildMcpServerOptions();
		using var cts = new CancellationTokenSource();
		var roundTrips = 0;

		var session = await StartAsync(
			mcpOptions,
			BuildRootsClientOptions("file:///ga", () => Interlocked.Increment(ref roundTrips)),
			cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		var text = await CallAsync(session, "twice", cts.Token).ConfigureAwait(false);

		text.Should().Contain("1|1");
		Volatile.Read(ref roundTrips).Should().Be(1);
	}

	[TestMethod]
	[Description("Regression guard: a caller that gives up on roots must be released when ITS token fires, not when the service's internal fetch budget expires. The fetch is shared so one waiter abandoning it cannot cancel the answer the others are waiting for — but that is a reason to keep the fetch independent, not a reason to ignore the caller. A client that never answers roots/list would otherwise pin the handler for the full budget after the request is already gone.")]
	public async Task When_ACallerCancelsWhileRootsAreOutstanding_Then_ItIsReleasedOnItsOwnToken()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("give-up", async (IMcpClientRoots roots, CancellationToken ct) =>
		{
			using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
			caller.CancelAfter(CallerPatience);

			// Measured inside the handler: what is under test is how long the service holds this caller,
			// not how long the round-trip to the test client takes.
			var waited = Stopwatch.StartNew();
			try
			{
				await roots.GetAsync(caller.Token).ConfigureAwait(false);
				return $"resolved|{waited.ElapsedMilliseconds}";
			}
			catch (OperationCanceledException)
			{
				return $"cancelled|{waited.ElapsedMilliseconds}";
			}
		});

		var mcpOptions = app.BuildMcpServerOptions();
		using var cts = new CancellationTokenSource();

		var session = await StartAsync(mcpOptions, BuildSlowRootsClientOptions(), cts.Token)
			.ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		// Trimmed because the tool result is JSON: the text block carries the surrounding quotes.
		var text = (await CallAsync(session, "give-up", cts.Token).ConfigureAwait(false)).Trim('"');
		var parts = text.Split('|');

		parts[0].Should().Be(
			"cancelled",
			because: "the caller gave up long before the client answered, so it must not receive the answer");
		int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
			.Should().BeLessThan(
				(int)ReleasedWithin.TotalMilliseconds,
				because: "the caller's token must release it, not the service's own 10-second fetch budget");
	}

	[TestMethod]
	[Description("Regression guard: an MCP App UI resource handler must see the flowing request like every other prebuilt primitive. On the reusable-options path the SDK dispatches straight into the resource, so a handler injecting a capability service resolves it against nothing and reports a capable client as incapable — the same defect the tool path was fixed for, in the one primitive that never bound its request.")]
	public async Task When_AUiResourceHandlerInjectsACapability_Then_ItResolvesAgainstTheCallersRequest()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();

		// Configured here rather than on UseMcpServer: BuildMcpServerOptions builds its own
		// ReplMcpServerOptions, so this is the callback that reaches the reusable-options path.
		var mcpOptions = app.BuildMcpServerOptions(options => options.UiResource(
			"ui://probe/capability",
			(IMcpSampling sampling) => sampling.IsSupported
				? "<!doctype html><html><body>sampling-on</body></html>"
				: "<!doctype html><html><body>sampling-off</body></html>"));
		using var cts = new CancellationTokenSource();

		var capable = await StartAsync(mcpOptions, BuildSamplingClientOptions(), cts.Token).ConfigureAwait(false);
		await using var capableScope = capable.ConfigureAwait(false);
		var plain = await StartAsync(mcpOptions, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var plainScope = plain.ConfigureAwait(false);

		var capableHtml = await ReadUiAsync(capable, cts.Token).ConfigureAwait(false);
		var plainHtml = await ReadUiAsync(plain, cts.Token).ConfigureAwait(false);

		capableHtml.Should().Contain("sampling-on");
		plainHtml.Should().Contain("sampling-off");
	}

	[TestMethod]
	[Description("Regression guard: a roots/list that fails must be retracted, not memoised. The per-request entry shares one fetch so that resolving roots twice costs one round-trip — which must not turn a single transient failure into a permanent one for the rest of the request. A later call has to be able to try again.")]
	public async Task When_TheFirstRootsRequestFails_Then_ALaterCallInTheSameRequestTriesAgain()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("retry", async (IMcpClientRoots roots, CancellationToken ct) =>
		{
			try
			{
				await roots.GetAsync(ct).ConfigureAwait(false);
				return "first-call-should-have-failed";
			}
			catch (Exception)
			{
				var retried = await roots.GetAsync(ct).ConfigureAwait(false);
				return string.Join(',', retried.Select(root => root.Uri.ToString()));
			}
		});

		var mcpOptions = app.BuildMcpServerOptions();
		using var cts = new CancellationTokenSource();

		var session = await StartAsync(mcpOptions, BuildFlakyRootsClientOptions(), cts.Token)
			.ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		var text = await CallAsync(session, "retry", cts.Token).ConfigureAwait(false);

		text.Should().Contain("file:///ga");
	}

	[TestMethod]
	[Description("Regression guard: the cross-product of the two roots guards above — the last waiter stops waiting AND the shared fetch fails afterwards. Nothing is left to observe that failure, so a fetch retracted only when a waiter sees it throw stays cached, and the next call in the same request replays a failure that is already over instead of issuing the promised retry.")]
	public async Task When_TheLastWaiterLeavesBeforeTheFetchFails_Then_ALaterCallStillRetries()
	{
		var firstAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("abandon-then-retry", async (IMcpClientRoots roots, CancellationToken ct) =>
		{
			using var impatient = CancellationTokenSource.CreateLinkedTokenSource(ct);
			impatient.CancelAfter(CallerPatience);
			try
			{
				await roots.GetAsync(impatient.Token).ConfigureAwait(false);
				return "the-first-call-should-have-been-abandoned";
			}
			catch (OperationCanceledException)
			{
				// Expected: nobody is waiting on the fetch from here on.
			}

			// The fetch fails after its last waiter has gone: the client answers with an unparseable
			// URI, so mapping it throws server-side, with nothing left to observe the failure.
			await firstAnswered.Task.WaitAsync(ct).ConfigureAwait(false);
			await Task.Delay(FaultSettlingDelay, ct).ConfigureAwait(false);

			var recovered = await roots.GetAsync(ct).ConfigureAwait(false);
			return string.Join(',', recovered.Select(root => root.Uri.ToString()));
		});

		var mcpOptions = app.BuildMcpServerOptions();
		using var cts = new CancellationTokenSource();

		var session = await StartAsync(
			mcpOptions,
			BuildAbandonedThenRecoveringRootsClientOptions(firstAnswered),
			cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		var text = await CallAsync(session, "abandon-then-retry", cts.Token).ConfigureAwait(false);

		text.Should().Contain(
			"file:///recovered",
			because: "the failed fetch must be retracted even though no caller was left to observe it");
	}

	[TestMethod]
	[Description("Regression guard: the Apps extension capability must cover whatever the catalog can contain. Capabilities are declared once, before any request names an era or a caller, while the catalog that reaches a client is resolved later — so an App behind a presence gate must be advertised anyway, or a client that is served it holds metadata with nothing to interpret it.")]
	public async Task When_ACapabilityGatedAppIsInTheCatalog_Then_TheAppsExtensionIsAdvertised()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.MapModule(new GatedAppModule(), (IMcpClientRoots roots) => roots.IsSupported);

		var mcpOptions = app.BuildMcpServerOptions();

#pragma warning disable MCPEXP001
		mcpOptions.Capabilities?.Extensions.Should().NotBeNull()
			.And.ContainKey(
				McpAppMetadata.ExtensionName,
				because: "the catalog this options instance serves contains an MCP App resource");
#pragma warning restore MCPEXP001
	}

	[TestMethod]
	[Description("Guards the opposite direction of the same rule: an MCP App behind a negated capability gate must be advertised too. Any probe that answered by evaluating the gates would get exactly one of these two cases wrong, whichever way it resolved them, so both stay pinned.")]
	public async Task When_AnAppIsGatedOnAMissingCapability_Then_TheAppsExtensionIsStillAdvertised()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.MapModule(new GatedAppModule(), (IMcpClientRoots roots) => !roots.IsSupported);

		var mcpOptions = app.BuildMcpServerOptions();

#pragma warning disable MCPEXP001
		mcpOptions.Capabilities?.Extensions.Should().NotBeNull()
			.And.ContainKey(
				McpAppMetadata.ExtensionName,
				because: "this catalog registers an MCP App resource, and what the gate would decide is "
					+ "not knowable when capabilities are declared");
#pragma warning restore MCPEXP001
	}

	private sealed class GatedAppModule : IReplModule
	{
		public void Map(IReplMap app) =>
			app.Map("dashboard", () => "<!doctype html><html><body>ok</body></html>")
				.ReadOnly()
				.AsMcpAppResource("ui://gated/dashboard");
	}

	private static async Task<string> ReadUiAsync(McpPipeSession session, CancellationToken cancellationToken)
	{
		var result = await session.Client.ReadResourceAsync(
			uri: "ui://probe/capability",
			cancellationToken: cancellationToken).ConfigureAwait(false);

		return result.Contents.OfType<TextResourceContents>().Single().Text;
	}

	private static Task<string> CallProbeAsync(McpPipeSession session, CancellationToken cancellationToken) =>
		CallAsync(session, "probe", cancellationToken);

	private static async Task<string> CallAsync(
		McpPipeSession session,
		string toolName,
		CancellationToken cancellationToken)
	{
		var result = await session.Client.CallToolAsync(
			toolName: toolName,
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cancellationToken).ConfigureAwait(false);

		return result.Content.OfType<TextContentBlock>().First().Text;
	}

	/// <summary>
	/// Starts one connection over the shared <paramref name="mcpOptions"/>, mirroring the sample in
	/// docs/mcp-transports.md — including passing no service provider to <c>McpServer.Create</c>.
	/// </summary>
	[TestMethod]
	[Description("Regression guard: a reusable BuildMcpServerOptions() catalog is frozen once, with the modern discovery view, whatever era a client later negotiates. Execution must therefore keep that same view for deciding presence even when the request is an initialize-era one \u2014 keying it on the request instead leaves a legacy client holding a catalog entry it cannot call, which is the same advertised-but-unreachable defect the dynamic path had.")]
	public async Task When_ALegacyClientCallsAGatedToolFromTheStaticCatalog_Then_TheCommandRuns()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("always", () => "ok");
		app.MapModule(new GatedModule(), (IMcpClientRoots roots) => roots.IsSupported);
		var mcpOptions = app.BuildMcpServerOptions();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var session = await StartAsync(mcpOptions, BuildLegacyClientOptions(), cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);
		session.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.LastWithSessions);

		(await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Should().Contain(
				tool => string.Equals(tool.Name, "gated", StringComparison.Ordinal),
				because: "the static catalog is built with the modern view, so this tool is offered");

		var result = await session.Client.CallToolAsync(
			toolName: "gated",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cts.Token).ConfigureAwait(false);

		var text = string.Join(
			separator: '\n',
			values: result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

		text.Should().Contain(
			"roots-only",
			because: "what this catalog advertised must be callable by the client it advertised it to");
	}

	/// <summary>A client that negotiates the initialize era and declares no capabilities.</summary>
	private static McpClientOptions BuildLegacyClientOptions() =>
		new() { ProtocolVersion = McpProtocolRevisions.LastWithSessions };

	private sealed class GatedModule : IReplModule
	{
		public void Map(IReplMap app) => app.Map("gated", () => "roots-only").ReadOnly();
	}

	private static Task<McpPipeSession> StartAsync(
		McpServerOptions mcpOptions,
		McpClientOptions? clientOptions,
		CancellationToken cancellationToken) =>
		McpPipeSession.StartAsync(
			async (io, token) =>
			{
				var transport = new StreamServerTransport(io.InputStream, io.OutputStream, "shared-options-server");
				var server = McpServer.Create(transport, mcpOptions);
				try
				{
					await server.RunAsync(token).ConfigureAwait(false);
				}
				finally
				{
					await server.DisposeAsync().ConfigureAwait(false);
					await transport.DisposeAsync().ConfigureAwait(false);
				}
			},
			clientOptions,
			cancellationToken);

	/// <summary>How long the caller in the cancellation guard waits before giving up.</summary>
	private static readonly TimeSpan CallerPatience = TimeSpan.FromMilliseconds(200);

	/// <summary>
	/// How long the client in that guard sits on <c>roots/list</c> before answering. Far longer than
	/// <see cref="CallerPatience"/>, so which of the two the service honoured decides the outcome
	/// rather than a race: waiting on the caller's token cancels, waiting on the fetch resolves.
	/// It cannot simply never answer — the client dispatches server requests on its read loop, so a
	/// blocked roots handler would also stop it reading the tool response.
	/// </summary>
	private static readonly TimeSpan ClientSilence = TimeSpan.FromSeconds(5);

	/// <summary>
	/// The bound the caller must be released within. Generous against <see cref="CallerPatience"/> and
	/// still far under <see cref="ClientSilence"/>, so it pins that the release came from the caller's
	/// own token and not from the fetch finishing.
	/// </summary>
	private static readonly TimeSpan ReleasedWithin = TimeSpan.FromSeconds(2.5);

	/// <summary>How long the abandoned <c>roots/list</c> runs on after its only waiter has left.</summary>
	private static readonly TimeSpan AbandonedFetchDelay = TimeSpan.FromMilliseconds(600);

	/// <summary>Time allowed for that answer to reach the server and fail while being mapped.</summary>
	private static readonly TimeSpan FaultSettlingDelay = TimeSpan.FromMilliseconds(400);

	// Roots and sampling are deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005) but still supported
	// by Repl.Mcp until the SDK removes the surface (#51).
#pragma warning disable MCP9005
	/// <summary>A client that declares roots and takes <see cref="ClientSilence"/> to answer.</summary>
	private static McpClientOptions BuildSlowRootsClientOptions() => new()
	{
		Capabilities = new ClientCapabilities
		{
			Roots = new RootsCapability { ListChanged = true },
		},
		Handlers = new McpClientHandlers
		{
			RootsHandler = async (_, token) =>
			{
				await Task.Delay(ClientSilence, token).ConfigureAwait(false);
				return new ListRootsResult { Roots = [new Root { Uri = "file:///slow", Name = "slow" }] };
			},
		},
	};

	private static McpClientOptions BuildRootsClientOptions(string rootUri, Action? onRequest = null) => new()
	{
		Capabilities = new ClientCapabilities
		{
			Roots = new RootsCapability { ListChanged = true },
		},
		Handlers = new McpClientHandlers
		{
			RootsHandler = (_, _) =>
			{
				onRequest?.Invoke();
				return ValueTask.FromResult(new ListRootsResult
				{
					Roots = [new Root { Uri = rootUri, Name = rootUri }],
				});
			},
		},
	};

	/// <summary>
	/// A client whose first <c>roots/list</c> outlives its waiter and then answers unusably, and whose
	/// next one answers normally.
	/// </summary>
	private static McpClientOptions BuildAbandonedThenRecoveringRootsClientOptions(
		TaskCompletionSource firstAnswered)
	{
		var calls = 0;
		return new McpClientOptions
		{
			Capabilities = new ClientCapabilities
			{
				Roots = new RootsCapability { ListChanged = true },
			},
			Handlers = new McpClientHandlers
			{
				RootsHandler = async (_, token) =>
				{
					if (Interlocked.Increment(ref calls) > 1)
					{
						return new ListRootsResult
						{
							Roots = [new Root { Uri = "file:///recovered", Name = "recovered" }],
						};
					}

					await Task.Delay(AbandonedFetchDelay, token).ConfigureAwait(false);
					firstAnswered.TrySetResult();
					return new ListRootsResult
					{
						Roots = [new Root { Uri = "http://", Name = "unparseable" }],
					};
				},
			},
		};
	}

	/// <summary>A client that fails the first <c>roots/list</c> and answers every later one.</summary>
	private static McpClientOptions BuildFlakyRootsClientOptions()
	{
		var calls = 0;
		return new McpClientOptions
		{
			Capabilities = new ClientCapabilities
			{
				Roots = new RootsCapability { ListChanged = true },
			},
			Handlers = new McpClientHandlers
			{
				// The first answer carries an unparseable URI, so the failure happens server-side while
				// mapping the result — a real fetch failure. A handler that throws instead would escape
				// the client's own message loop rather than failing the request on the wire.
				RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult
				{
					Roots = Interlocked.Increment(ref calls) == 1
						? [new Root { Uri = "http://", Name = "unparseable" }]
						: [new Root { Uri = "file:///ga", Name = "ga" }],
				}),
			},
		};
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
#pragma warning restore MCP9005
}
