using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// The DI scope an MCP connection owns. One scope per session — not per invocation — so discovery and
/// execution read the same instances and a command can carry state across the calls of one connection.
/// </summary>
[TestClass]
public sealed class Given_McpSessionScopes
{
	private sealed class ScopedProbe
	{
		public Guid Id { get; } = Guid.NewGuid();

		public bool SignedIn { get; set; }
	}

	private sealed class DisposableProbe(List<Guid> disposed) : IDisposable
	{
		public Guid Id { get; } = Guid.NewGuid();

		public void Dispose() => disposed.Add(Id);
	}

	private sealed class GatedModule : IReplModule
	{
		public void Map(IReplMap app) => app.Map("secret", () => "classified").ReadOnly();
	}

	private sealed class RecordingSessionState : IReplSessionState
	{
		public static int Constructed;

		public RecordingSessionState() => Interlocked.Increment(ref Constructed);

		private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

		public bool TryGet<T>(string key, out T? value)
		{
			if (_values.TryGetValue(key, out var existing) && existing is T typed)
			{
				value = typed;
				return true;
			}

			value = default;
			return false;
		}

		public T? Get<T>(string key) => TryGet<T>(key, out var value) ? value : default;

		public void Set<T>(string key, T value) => _values[key] = value;

		public bool Remove(string key) => _values.Remove(key);

		public void Clear() => _values.Clear();
	}

	[TestMethod]
	[Description("Issue #70 criterion 1 on MCP: two commands of one session must share their Scoped instance. Adopting the SDK's per-request scope (McpServerOptions.ScopeRequests defaults to true) would give each tool call a fresh instance, so a cart or an auth context could never survive from one call to the next on a connection.")]
	public async Task When_TwoToolCallsShareAConnection_Then_TheyShareOneScopedInstance()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("scoped", (ScopedProbe probe) => probe.Id.ToString()),
			services => services.AddScoped<ScopedProbe>()).ConfigureAwait(false);

		var first = await CallAsync(fixture.Client, "scoped").ConfigureAwait(false);
		var second = await CallAsync(fixture.Client, "scoped").ConfigureAwait(false);

		first.Should().NotBeNullOrWhiteSpace();
		first.Should().Be(second, "one connection is one session, and one session is one scope");
	}

	[TestMethod]
	[Description("Issue #70 criterion 6: concurrent MCP sessions must not observe each other's Scoped per-user state. Before the session scope existed, every connection resolved from the application root and shared one instance.")]
	public async Task When_TwoConnectionsShareOneServer_Then_EachResolvesItsOwnScopedInstance()
	{
		var app = ReplApp.Create(services => services.AddScoped<ScopedProbe>());
		app.UseMcpServer();
		app.Map("scoped", (ScopedProbe probe) => probe.Id.ToString());

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var handler = CreateHandler(app);

		var first = await StartAsync(handler, cts.Token).ConfigureAwait(false);
		await using var firstScope = first.ConfigureAwait(false);
		var second = await StartAsync(handler, cts.Token).ConfigureAwait(false);
		await using var secondScope = second.ConfigureAwait(false);

		var fromFirst = await CallAsync(first.Client, "scoped", token: cts.Token).ConfigureAwait(false);
		var fromSecond = await CallAsync(second.Client, "scoped", token: cts.Token).ConfigureAwait(false);

		fromFirst.Should().NotBe(fromSecond, "another connection is another session");
	}

	[TestMethod]
	[Description("Issue #70 criterion 3: a Scoped IDisposable resolved during a session is released when that session ends, not held for the life of the server process. The session scope is disposed with its McpSessionContext when the connection's RunAsync returns.")]
	public async Task When_AConnectionEnds_Then_ItsScopedDisposablesAreDisposed()
	{
		var disposed = new List<Guid>();
		var app = ReplApp.Create(services => services.AddScoped(_ => new DisposableProbe(disposed)));
		app.UseMcpServer();
		app.Map("disposable", (DisposableProbe probe) => probe.Id.ToString());

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var handler = CreateHandler(app);

		string reported;
		var session = await StartAsync(handler, cts.Token).ConfigureAwait(false);
		await using (session.ConfigureAwait(false))
		{
			reported = await CallAsync(session.Client, "disposable", token: cts.Token).ConfigureAwait(false);
			disposed.Should().BeEmpty("the session is still open");
		}

		disposed.Should().ContainSingle();
		reported.Should().Contain(
			disposed[0].ToString(),
			"the disposed instance must be the one the command was handed");
	}

	[TestMethod]
	[Description("Regression guard for the invariant PresenceServiceProvider exists to hold: what discovery advertised must be callable. Discovery and execution have to resolve a user Scoped service from the SAME instance — resolve execution from a per-invocation scope instead and a module gated on one is advertised after a tool sets it, then found absent when called. docs/module-presence.md recommends exactly this pattern.")]
	public async Task When_APresencePredicateGatesOnAScopedService_Then_TheAdvertisedToolIsCallable()
	{
		var app = ReplApp.Create(services => services.AddScoped<ScopedProbe>());
		app.UseMcpServer();
		app.Map("signin", (ScopedProbe probe, ICoreReplApp core) =>
		{
			probe.SignedIn = true;
			core.InvalidateRouting();
			return "ok";
		});
		app.MapModule(new GatedModule(), (ScopedProbe probe) => probe.SignedIn);

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var handler = CreateHandler(app);
		var session = await StartAsync(
			handler,
			cts.Token,
			new McpClientOptions { ProtocolVersion = McpProtocolRevisions.LastWithSessions }).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		(await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Should().NotContain(tool => string.Equals(tool.Name, "secret", StringComparison.Ordinal));

		await CallAsync(session.Client, "signin", token: cts.Token).ConfigureAwait(false);

		(await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Should().Contain(
				tool => string.Equals(tool.Name, "secret", StringComparison.Ordinal),
				"the predicate reads the instance the tool call just wrote");

		var body = await CallAsync(session.Client, "secret", token: cts.Token).ConfigureAwait(false);
		body.Should().Contain("classified", "what discovery advertised has to run");
	}

	[TestMethod]
	[Description("Guards the composition order. The session's capability services are layered OVER its DI scope, so a command resolves both; get it backwards and IMcpClientRoots resolves nothing — and because McpServiceProviderOverlay also answers IServiceProviderIsService, a declared capability is reclassified as a client-supplied argument and the primitive stops being invocable.")]
	public async Task When_ACommandInjectsACapabilityService_Then_ItResolvesAlongsideTheSessionScope()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map(
				"capability",
				(IMcpClientRoots roots, IMcpFeedback feedback, ScopedProbe probe) =>
					$"{roots is not null}|{feedback is not null}|{probe.Id}"),
			services => services.AddScoped<ScopedProbe>()).ConfigureAwait(false);

		var first = await CallAsync(fixture.Client, "capability").ConfigureAwait(false);
		var second = await CallAsync(fixture.Client, "capability").ConfigureAwait(false);

		first.Should().Contain("True|True|");
		first.Should().Be(second, "the scoped probe beside the capabilities is the session's own");
	}

	[TestMethod]
	[Description("The MCP half of issue #70 for the framework's own session service: IReplSessionState fell through to the application root, so two concurrent connections shared one bag. It now resolves from the session scope, which is also what keeps the initialize-era dynamic-tools story working.")]
	public async Task When_TwoConnectionsShareOneServer_Then_NeitherReadsTheOtherSessionState()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("remember {value}", (IReplSessionState state, string value) =>
		{
			state.Set("carried", value);
			return "stored";
		});
		app.Map("recall", (IReplSessionState state) => state.Get<string>("carried") ?? "(none)");

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var handler = CreateHandler(app);

		var first = await StartAsync(handler, cts.Token).ConfigureAwait(false);
		await using var firstScope = first.ConfigureAwait(false);
		var second = await StartAsync(handler, cts.Token).ConfigureAwait(false);
		await using var secondScope = second.ConfigureAwait(false);

		await CallAsync(
			first.Client,
			"remember",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = "alpha" },
			cts.Token).ConfigureAwait(false);

		var carried = await CallAsync(first.Client, "recall", token: cts.Token).ConfigureAwait(false);
		var other = await CallAsync(second.Client, "recall", token: cts.Token).ConfigureAwait(false);

		carried.Should().Contain("alpha", "session state survives across the calls of its own connection");
		other.Should().Contain("(none)", "another connection is another session");
	}

	[TestMethod]
	[Description("A consumer who registers their own IReplSessionState must get it on MCP too. Seeding the session's override map with a framework instance would win unconditionally over the container, so the one transport where per-connection state matters most would silently run a different implementation from the one the app composed.")]
	public async Task When_AConsumerRegistersItsOwnSessionState_Then_McpResolvesIt()
	{
		RecordingSessionState.Constructed = 0;
		var app = ReplApp.Create(services => services.AddScoped<IReplSessionState, RecordingSessionState>());
		app.UseMcpServer();
		app.Map("kind", (IReplSessionState state) => state.GetType().Name);

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var handler = CreateHandler(app);
		var session = await StartAsync(handler, cts.Token).ConfigureAwait(false);
		await using var scope = session.ConfigureAwait(false);

		var reported = await CallAsync(session.Client, "kind", token: cts.Token).ConfigureAwait(false);

		reported.Should().Contain(nameof(RecordingSessionState));
		RecordingSessionState.Constructed.Should().BeGreaterThan(0);
	}

	[TestMethod]
	[Description("Pins the documented carve-out on the reusable BuildMcpServerOptions() path: that server has no connection row, so its one handler-lifetime context — and therefore one DI scope — is shared by every connection it serves, exactly as its native roots cache and soft roots are. Stated here so it stays a known limitation rather than becoming a silent cross-client leak.")]
	public async Task When_ConnectionsShareAReusableOptionsResult_Then_TheyShareOneScope()
	{
		var app = ReplApp.Create(services => services.AddScoped<ScopedProbe>());
		app.Map("scoped", (ScopedProbe probe) => probe.Id.ToString());
		var options = app.BuildMcpServerOptions();

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var first = await StartSharedAsync(options, cts.Token).ConfigureAwait(false);
		await using var firstScope = first.ConfigureAwait(false);
		var second = await StartSharedAsync(options, cts.Token).ConfigureAwait(false);
		await using var secondScope = second.ConfigureAwait(false);

		var fromFirst = await CallAsync(first.Client, "scoped", token: cts.Token).ConfigureAwait(false);
		var fromSecond = await CallAsync(second.Client, "scoped", token: cts.Token).ConfigureAwait(false);

		fromFirst.Should().NotBeNullOrWhiteSpace();
		fromFirst.Should().Be(
			fromSecond,
			"a reused options result has one context for every connection, so it has one scope too");
	}

	private static Task<McpPipeSession> StartSharedAsync(
		McpServerOptions mcpOptions,
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
			clientOptions: null,
			cancellationToken);

	private static McpServerHandler CreateHandler(ReplApp app) =>
		new(
			app.Core,
			new ReplMcpServerOptions { TransportFactory = McpTestFixture.PipeTransportFactory },
			app.Services);

	private static Task<McpPipeSession> StartAsync(
		McpServerHandler handler,
		CancellationToken cancellationToken,
		McpClientOptions? clientOptions = null) =>
		McpPipeSession.StartAsync(handler.RunAsync, clientOptions, cancellationToken);

	private static async Task<string> CallAsync(
		McpClient client,
		string tool,
		Dictionary<string, object?>? arguments = null,
		CancellationToken token = default)
	{
		var result = await client.CallToolAsync(
			toolName: tool,
			arguments: arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: token).ConfigureAwait(false);
		return result.Content.OfType<TextContentBlock>().First().Text;
	}
}
