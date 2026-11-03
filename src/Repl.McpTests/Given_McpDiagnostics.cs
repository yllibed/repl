using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Repl.Documentation;
using Repl.Mcp;
using Repl.Parameters;

// Deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005); kept for existing hosts.
// Rationale and successor: docs/mcp-reference.md#sdk-and-protocol-versions (#51).
#pragma warning disable MCP9005

namespace Repl.McpTests;

/// <summary>
/// Repl.Mcp absorbs four failures by design: a tool failure's detail is withheld from the client, a
/// failed catalog build is hidden behind the previous catalog, a failed roots prime is swallowed, and the
/// option behind a hidden-required failure is withheld from the client. Each must still reach the
/// operator's log — once per episode or routing version, not once per request that retries it.
/// </summary>
[TestClass]
public sealed class Given_McpDiagnostics
{
	// The category and event ids are what an operator filters on, so the tests pin them rather than
	// matching any entry that happens to carry an exception: the capturing provider only looks at this
	// category, and finds entries by event id.
	private const string Category = "Repl.Mcp";
	private const int ToolFailureWithheldEvent = 2001;
	private const int StaleCatalogServedEvent = 2002;
	private const int RootsPrimeFailedEvent = 2003;
	private const int StaleCatalogStillServedEvent = 2004;
	private const int CatalogRecoveredEvent = 2005;
	private const int RootsPrimeStillFailingEvent = 2006;
	private const int RootsPrimeRecoveredEvent = 2007;
	private const int HiddenRequiredOptionWithheldEvent = 2008;
	private const int HiddenRequiredOptionStillWithheldEvent = 2009;

	[TestMethod]
	[Description("A tool call that fails with a withheld detail (an unhandled exception, not McpException/McpInteractionException) must still record that detail for the operator: the client's generic 'Command failed' message is deliberate, discarding invocation.Failure and its rendered text entirely is not.")]
	public async Task When_AToolFailureDetailIsWithheld_Then_TheOperatorSinkRecordsIt()
	{
		var provider = new CapturingLoggerProvider();
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("boom", object () => throw new InvalidOperationException("secret-detail")),
			services => CaptureLogs(services, provider)).ConfigureAwait(false);

		var result = await CallAsync(fixture.Client, "boom").ConfigureAwait(false);

		result.Should().NotContain("secret-detail", "the withheld detail must not reach the remote client");
		var entry = provider.Single(ToolFailureWithheldEvent);
		entry.Level.Should().Be(LogLevel.Error);
		entry.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("secret-detail");
		entry.Message.Should().Contain("secret-detail", "the rendered detail the client did not get belongs in the message");
		entry.Message.Should().Contain(
			"boom failed",
			"the entry is written after the pipeline's logging scope has ended, so only its own text can say which tool failed");
	}

	[TestMethod]
	[Description("Roots priming swallows everything but the caller's own cancellation by design: every execution entry point primes, and a handler that never reads roots must not fail because the client could not answer. The failure must still reach the operator — but a fast failure is retried on every tool call, so only the first of an episode warns and the repeats go to Debug.")]
	public async Task When_RootsPrimingKeepsFailing_Then_OnlyTheFirstFailureWarns()
	{
		var provider = new CapturingLoggerProvider();
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("touch", () => "ok"),
			configureOptions: null,
			clientOptions: CreateRootsClient(),
			configureServices: services => CaptureLogs(services, provider)).ConfigureAwait(false);

		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);

		var warning = provider.Single(RootsPrimeFailedEvent);
		warning.Level.Should().Be(LogLevel.Warning);
		warning.Exception.Should().BeOfType<UriFormatException>("the client answered roots/list with an unparseable URI");
		provider.Count(RootsPrimeStillFailingEvent).Should().Be(0, "the first failure of an episode is the one that warns");

		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);

		provider.Count(RootsPrimeFailedEvent).Should().Be(1, "a repeat of the same episode must not warn again");
		provider.Count(RootsPrimeStillFailingEvent).Should().Be(1, "the repeat is still recorded");
		provider.Count(LogLevel.Warning).Should().Be(1, "the repeat is recorded at Debug, below the threshold an operator reads");
	}

	[TestMethod]
	[Description("A prime that succeeds ends the failure episode, and says so: without a recovery entry, a quiet log at Information reads the same as a client that is still failing on every call. It also re-arms the warning, so the next outage on that connection warns again instead of going straight to Debug.")]
	public async Task When_RootsPrimingRecovers_Then_TheRecoveryIsLoggedAndTheNextFailureWarns()
	{
		var provider = new CapturingLoggerProvider();
		var answersValidly = 0;
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("touch", () => "ok"),
			configureOptions: null,
			clientOptions: CreateRootsClient(answersValidly: () => Volatile.Read(ref answersValidly) == 1),
			configureServices: services => CaptureLogs(services, provider)).ConfigureAwait(false);

		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);
		provider.Count(RootsPrimeFailedEvent).Should().Be(1);

		Volatile.Write(ref answersValidly, 1);
		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);

		var recovery = provider.Single(RootsPrimeRecoveredEvent);
		recovery.Level.Should().Be(LogLevel.Information);

		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);
		provider.Count(RootsPrimeRecoveredEvent).Should().Be(
			1,
			"a prime answered from the cache also succeeds, and only the one that ends a failure is a recovery");

		// Roots are cached once a prime succeeds, so only the client saying they changed makes the next
		// call ask again. The routing invalidation the notification raises orders it before that call.
		Volatile.Write(ref answersValidly, 0);
		var cacheCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.App.Core.RoutingInvalidated += (_, _) => cacheCleared.TrySetResult();
		await fixture.Client.SendNotificationAsync(NotificationMethods.RootsListChangedNotification).ConfigureAwait(false);
		await cacheCleared.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);

		provider.Count(RootsPrimeFailedEvent).Should().Be(2, "the recovery ended the first episode, so this failure opens a new one");
		provider.Count(RootsPrimeStillFailingEvent).Should().Be(0);
	}

	[TestMethod]
	[Description("A prime skipped because the connection is standing down after a slow failure is not a successful prime, so it must not end the failure episode. Resetting on any normal return made the next real failure — once the stand-down lifts — warn again, so one ongoing outage produced repeated 2003 warnings. roots/list_changed lifts the stand-down on purpose; the routing invalidation it raises right after, in the same handler, orders the lift before the next call.")]
	public async Task When_AStandDownSkipsThePrime_Then_TheFailureEpisodeIsNotReset()
	{
		var provider = new CapturingLoggerProvider();
		var fetches = 0;
		var slowNext = 0;
		// Longer than half the ten-second roots budget, which is what makes a failure worth standing
		// down for; well under the budget itself, so the fetch fails on the bad URI rather than timing out.
		var slowFailure = TimeSpan.FromSeconds(5.5);
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("touch", () => "ok"),
			configureOptions: null,
			clientOptions: CreateRootsClient(async cancellationToken =>
			{
				Interlocked.Increment(ref fetches);
				if (Interlocked.Exchange(ref slowNext, 0) == 1)
				{
					await Task.Delay(slowFailure, cancellationToken).ConfigureAwait(false);
				}
			}),
			configureServices: services => CaptureLogs(services, provider)).ConfigureAwait(false);
		var standDownLifted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.App.Core.RoutingInvalidated += (_, _) => standDownLifted.TrySetResult();

		// Opens the episode with a fast failure, which does not stand the prime down.
		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);
		provider.Count(RootsPrimeFailedEvent).Should().Be(1);

		Volatile.Write(ref slowNext, 1);
		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);
		var afterSlowFailure = Volatile.Read(ref fetches);
		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);
		Volatile.Read(ref fetches).Should().Be(afterSlowFailure, "the slow failure stood the prime down, so this call asked nothing");

		await fixture.Client.SendNotificationAsync(NotificationMethods.RootsListChangedNotification).ConfigureAwait(false);
		await standDownLifted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);

		Volatile.Read(ref fetches).Should().BeGreaterThan(afterSlowFailure, "roots/list_changed lifted the stand-down");
		provider.Count(RootsPrimeFailedEvent).Should().Be(1, "the skipped prime did not end the episode, so no failure in it warns again");
	}

	[TestMethod]
	[Description("The availability fallback re-serves an initialize-era connection's previous catalog when a build fails, and republishes it stale, so every request retries the build. The failure must reach the operator — once, as a warning, rather than once per request for as long as it lasts — and the end of the episode must be visible too, or a quiet log would read as 'still failing'.")]
	public async Task When_AStaleCatalogIsServedUntilRecovery_Then_ItWarnsOnceAndLogsTheRecovery()
	{
		var provider = new CapturingLoggerProvider();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (_, options, session) = await StartFailingLegacyCatalogAsync(provider, cts.Token).ConfigureAwait(false);
		await using var sessionScope = session.ConfigureAwait(false);

		var stale = await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		stale.Should().ContainSingle(
			tool => string.Equals(tool.Name, "initial", StringComparison.Ordinal),
			"the fallback itself is unchanged: the previous catalog is still served");
		var warning = provider.Single(StaleCatalogServedEvent);
		warning.Level.Should().Be(LogLevel.Warning);
		warning.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("projection-boom");
		provider.Count(StaleCatalogStillServedEvent).Should().Be(0, "the first failure of an episode is the one that warns");

		await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		provider.Count(StaleCatalogServedEvent).Should().Be(1, "a retry of the same failing version must not warn again");
		provider.Count(StaleCatalogStillServedEvent).Should().BeGreaterThan(0, "the retry's failure is still recorded");
		provider.Count(LogLevel.Warning).Should().Be(1, "the retry is recorded at Debug, below the threshold an operator reads");
		provider.Count(CatalogRecoveredEvent).Should().Be(0, "nothing has recovered yet");

		options.CommandFilter = null;
		await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		var recovery = provider.Single(CatalogRecoveredEvent);
		recovery.Level.Should().Be(LogLevel.Information);
		provider.Count(StaleCatalogServedEvent).Should().Be(1, "recovering must not re-warn the episode it just ended");
	}

	[TestMethod]
	[Description("Recovery is logged for the catalog actually published, not for the version the build set out to serve. A rebuild that succeeds while routing moves under it is republished stale, so the connection does not serve the current catalog yet — announcing recovery then, with the version the build started at, would tell the operator a failure had ended that the next request may still hit.")]
	public async Task When_RoutingMovesDuringTheRecoveryBuild_Then_RecoveryWaitsForACurrentCatalog()
	{
		var provider = new CapturingLoggerProvider();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (app, options, session) = await StartFailingLegacyCatalogAsync(provider, cts.Token).ConfigureAwait(false);
		await using var sessionScope = session.ConfigureAwait(false);
		await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		provider.Count(StaleCatalogServedEvent).Should().Be(1);

		// The recovery build succeeds, but invalidates routing while it runs — the build it returns is
		// republished stale.
		var moveRoutingOnce = 1;
		options.CommandFilter = _ =>
		{
			if (Interlocked.Exchange(ref moveRoutingOnce, 0) == 1)
			{
				app.Core.InvalidateRouting();
			}

			return true;
		};
		await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		provider.Count(CatalogRecoveredEvent).Should().Be(0, "the catalog that build published is already stale");

		await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		provider.Single(CatalogRecoveredEvent).Message.Should().Contain(
			"version 3",
			"the recovery is the build at the routing version that moved during the first attempt");
	}

	[TestMethod]
	[Description("A recovery build that races a visibility retraction starts over at the newer version and publishes that one. The recovery must be logged at the version actually published, not the one the build set out to serve, or the operator is told the connection recovered at a routing version it never served.")]
	public async Task When_ARetractionRestartsTheRecoveryBuild_Then_RecoveryIsLoggedAtThePublishedVersion()
	{
		var provider = new CapturingLoggerProvider();
		CommandBuilder? extra = null;
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (_, options, session) = await StartFailingLegacyCatalogAsync(
			provider,
			cts.Token,
			app => extra = app.Map("extra", () => "x")).ConfigureAwait(false);
		await using var sessionScope = session.ConfigureAwait(false);

		// Version 1 was served; the failing rebuild is requested at version 2.
		await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		provider.Count(StaleCatalogServedEvent).Should().Be(1);

		// The recovery build at version 2 hides a command while it runs: a visibility retraction to
		// version 3, which makes the build discard its result and start over there.
		var retractOnce = 1;
		options.CommandFilter = _ =>
		{
			if (Interlocked.Exchange(ref retractOnce, 0) == 1)
			{
				extra!.Hidden();
			}

			return true;
		};
		(await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false))
			.Should().NotContain(tool => string.Equals(tool.Name, "extra", StringComparison.Ordinal));

		provider.Single(CatalogRecoveredEvent).Message.Should().Contain("version 3");
	}

	[TestMethod]
	[Description("A connection never served a catalog gets the same generic error as a served one (#104); only the operator log names the option's target, rendered token and route, which hiding the option withholds from the client.")]
	public async Task When_AColdConnectionHitsAHiddenRequiredOption_Then_OnlyTheOperatorLearnsWhichOne()
	{
		var provider = new CapturingLoggerProvider();
		// A CommandFilter defers projection to the first discovery request, so the server starts and the
		// failure reaches a connection that has never been served a catalog.
		await using var fixture = await McpTestFixture.CreateAsync(
			MapDeployWithAHiddenRequiredToken,
			static options => options.CommandFilter = static _ => true,
			clientOptions: null,
			configureServices: services => CaptureLogs(services, provider)).ConfigureAwait(false);

		var discover = async () => await fixture.Client.ListToolsAsync().ConfigureAwait(false);

		await ShouldFailWithoutNamingTheOptionAsync(discover).ConfigureAwait(false);
		ShouldHaveLoggedTheHiddenOption(provider);
	}

	[TestMethod]
	[Description("A connection already served a catalog fails closed with the same generic error; the withheld option and route must still reach the operator log, or nobody learns what to fix.")]
	public async Task When_AServedConnectionHitsAHiddenRequiredOption_Then_OnlyTheOperatorLearnsWhichOne()
	{
		var provider = new CapturingLoggerProvider();
		var exposeDeploy = false;
		await using var fixture = await McpTestFixture.CreateAsync(
			MapDeployWithAHiddenRequiredToken,
			options => options.CommandFilter = command =>
				exposeDeploy || !string.Equals(command.Path, "deploy", StringComparison.OrdinalIgnoreCase),
			clientOptions: null,
			configureServices: services => CaptureLogs(services, provider)).ConfigureAwait(false);
		await fixture.Client.ListToolsAsync().ConfigureAwait(false);

		exposeDeploy = true;
		fixture.App.Core.InvalidateRouting();
		var discover = async () => await fixture.Client.ListToolsAsync().ConfigureAwait(false);

		await ShouldFailWithoutNamingTheOptionAsync(discover).ConfigureAwait(false);
		ShouldHaveLoggedTheHiddenOption(provider);
	}

	[TestMethod]
	[Description("A hidden option the app also requires fails every discovery on every connection until the app changes, so it is an error once per routing version for the whole server, and its repeats are at Debug. Counted per connection, every client that connects would log the same misconfiguration as an error again.")]
	public async Task When_AHiddenRequiredOptionKeepsFailing_Then_EachRoutingVersionLogsOneError()
	{
		var provider = new CapturingLoggerProvider();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (app, first, second) = await StartTwoConnectionsAsync(provider, static _ => true, cts.Token).ConfigureAwait(false);
		await using var firstScope = first.ConfigureAwait(false);
		await using var secondScope = second.ConfigureAwait(false);

		await ShouldFailWithoutNamingTheOptionAsync(() => first.Client.ListToolsAsync(cancellationToken: cts.Token).AsTask()).ConfigureAwait(false);
		await ShouldFailWithoutNamingTheOptionAsync(() => second.Client.ListToolsAsync(cancellationToken: cts.Token).AsTask()).ConfigureAwait(false);

		provider.Count(HiddenRequiredOptionWithheldEvent).Should().Be(1, "another connection hitting the same version is a repeat");
		provider.Count(HiddenRequiredOptionStillWithheldEvent).Should().Be(1, "the repeat is still recorded");
		provider.Count(LogLevel.Error).Should().Be(1, "the repeat is recorded at Debug, below the threshold an operator reads");

		app.Core.InvalidateRouting();
		await ShouldFailWithoutNamingTheOptionAsync(() => first.Client.ListToolsAsync(cancellationToken: cts.Token).AsTask()).ConfigureAwait(false);

		provider.Count(HiddenRequiredOptionWithheldEvent).Should().Be(2, "a routing change that leaves the failure in place reports it again");
	}

	[TestMethod]
	[Description("Connections build at the routing version they read when their request started, so a slow build can fail at an older version after another connection has reported a newer one. That late failure is a repeat, not a new report: logging it as an error would report a version the server has already left.")]
	public async Task When_AnOlderVersionFailsAfterANewerOneWasReported_Then_ItIsLoggedAtDebug()
	{
		var provider = new CapturingLoggerProvider();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		using var slowBuildEntered = new ManualResetEventSlim();
		using var releaseSlowBuild = new ManualResetEventSlim();
		var holdFirstFilterCall = 1;
		// Holds the first build, the slow connection's, inside its projection at version 1.
		var (app, slow, fast) = await StartTwoConnectionsAsync(
			provider,
			_ =>
			{
				if (Interlocked.Exchange(ref holdFirstFilterCall, 0) == 1)
				{
					slowBuildEntered.Set();
					releaseSlowBuild.Wait(cts.Token);
				}

				return true;
			},
			cts.Token).ConfigureAwait(false);
		await using var slowScope = slow.ConfigureAwait(false);
		await using var fastScope = fast.ConfigureAwait(false);

		var slowDiscovery = ShouldFailWithoutNamingTheOptionAsync(() => slow.Client.ListToolsAsync(cancellationToken: cts.Token).AsTask());
		try
		{
			slowBuildEntered.Wait(cts.Token);
			app.Core.InvalidateRouting();
			await ShouldFailWithoutNamingTheOptionAsync(() => fast.Client.ListToolsAsync(cancellationToken: cts.Token).AsTask()).ConfigureAwait(false);
		}
		finally
		{
			// Released on failure too: the SDK waits for the held request before the session can close, so a
			// failed assertion above would otherwise surface as a dispose timeout instead.
			releaseSlowBuild.Set();
		}

		await slowDiscovery.ConfigureAwait(false);

		provider.Single(HiddenRequiredOptionWithheldEvent).Message.Should().Contain("version 2");
		provider.Single(HiddenRequiredOptionStillWithheldEvent).Message.Should().Contain(
			"version 1",
			"the slow connection's failure at version 1 came after version 2 was reported");
	}

	/// <summary>
	/// Starts two connections on one handler over an app whose <c>deploy</c> route hides a required option.
	/// The <paramref name="commandFilter"/> defers projection to each connection's first discovery request.
	/// </summary>
	private static async Task<(ReplApp App, McpPipeSession First, McpPipeSession Second)> StartTwoConnectionsAsync(
		CapturingLoggerProvider provider,
		Func<ReplDocCommand, bool> commandFilter,
		CancellationToken cancellationToken)
	{
		var app = ReplApp.Create(services => CaptureLogs(services, provider));
		app.UseMcpServer();
		MapDeployWithAHiddenRequiredToken(app);
		var options = new ReplMcpServerOptions
		{
			TransportFactory = McpTestFixture.PipeTransportFactory,
			CommandFilter = commandFilter,
		};
		var handler = new McpServerHandler(app.Core, options, app.Services);
		var first = await McpPipeSession.StartAsync(handler.RunAsync, clientOptions: null, cancellationToken).ConfigureAwait(false);
		try
		{
			var second = await McpPipeSession.StartAsync(handler.RunAsync, clientOptions: null, cancellationToken).ConfigureAwait(false);
			return (app, first, second);
		}
		catch
		{
			// The caller only takes ownership once this returns.
			await first.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	private static void MapDeployWithAHiddenRequiredToken(ReplApp app)
	{
		app.Map("ping", static () => "pong");
		app.Map(
			"deploy",
			static string ([ReplOption(Name = "token", Hidden = true, Arity = ReplArity.ExactlyOne)] string token) => token);
	}

	private static async Task ShouldFailWithoutNamingTheOptionAsync(Func<Task> discover)
	{
		var failure = (await discover.Should().ThrowAsync<McpException>().ConfigureAwait(false)).Which;
		failure.Message.Should().Contain("The server's MCP catalog is unavailable because of a server configuration error.");
		failure.Message.Should().NotContain("token", "the option's target and rendered token are what hiding it withholds");
		failure.Message.Should().NotContain("deploy", "the route is what hiding the option withholds");
	}

	private static void ShouldHaveLoggedTheHiddenOption(CapturingLoggerProvider provider)
	{
		var entry = provider.Single(HiddenRequiredOptionWithheldEvent);
		entry.Level.Should().Be(LogLevel.Error);
		entry.Exception.Should().BeAssignableTo<InvalidOperationException>();
		entry.Message.Should().Contain("'token'").And.Contain("'--token'").And.Contain("'deploy'");
	}

	private static void CaptureLogs(IServiceCollection services, CapturingLoggerProvider provider) =>
		services.AddLogging(builder =>
		{
			builder.ClearProviders();
			builder.SetMinimumLevel(LogLevel.Debug);
			builder.AddProvider(provider);
		});

	/// <summary>
	/// Starts an initialize-era session that has been served the catalog at routing version 1, then makes
	/// every later build fail and invalidates routing, so the next request falls back to that catalog.
	/// </summary>
	private static async Task<(ReplApp App, ReplMcpServerOptions Options, McpPipeSession Session)> StartFailingLegacyCatalogAsync(
		CapturingLoggerProvider provider,
		CancellationToken cancellationToken,
		Action<ReplApp>? mapMore = null)
	{
		var app = ReplApp.Create(services => CaptureLogs(services, provider));
		app.UseMcpServer();
		app.Map("initial", () => "ok");
		mapMore?.Invoke(app);
		var options = new ReplMcpServerOptions { TransportFactory = McpTestFixture.PipeTransportFactory };
		var handler = new McpServerHandler(app.Core, options, app.Services);

		var session = await McpPipeSession.StartAsync(
			handler.RunAsync,
			new McpClientOptions { ProtocolVersion = McpProtocolRevisions.LastWithSessions },
			cancellationToken).ConfigureAwait(false);
		try
		{
			await session.Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			// The caller only takes ownership once this returns.
			await session.DisposeAsync().ConfigureAwait(false);
			throw;
		}

		options.CommandFilter = _ => throw new InvalidOperationException("projection-boom");
		app.Core.InvalidateRouting();
		return (app, options, session);
	}

	private static McpClientOptions CreateRootsClient(
		Func<CancellationToken, Task>? beforeAnswer = null,
		Func<bool>? answersValidly = null) =>
		new()
		{
			Capabilities = new ClientCapabilities { Roots = new RootsCapability() },
			Handlers = new McpClientHandlers
			{
				// An unparseable URI fails server-side while being mapped — a real fetch failure that needs
				// no client-side throw, which would escape the SDK's own message loop instead.
				RootsHandler = async (_, cancellationToken) =>
				{
					if (beforeAnswer is not null)
					{
						await beforeAnswer(cancellationToken).ConfigureAwait(false);
					}

					var root = answersValidly?.Invoke() == true
						? new Root { Uri = "file:///workspace", Name = "workspace" }
						: new Root { Uri = "http://", Name = "invalid" };
					return new ListRootsResult { Roots = [root] };
				},
			},
		};

	private static async Task<string> CallAsync(McpClient client, string tool)
	{
		var result = await client.CallToolAsync(
			toolName: tool,
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);
		return result.Content.OfType<TextContentBlock>().First().Text;
	}

	// Concurrent because the server logs from its own threads while the test reads from another.
	private sealed class CapturingLoggerProvider : ILoggerProvider
	{
		private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

		public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

		// Only Repl.Mcp's entries: the SDK and Repl.Core log through the same provider, and their event ids and
		// levels are not the ones under test. A diagnostic logged under another category is then not found.
		private IEnumerable<CapturedLogEntry> McpEntries =>
			_entries.Where(entry => string.Equals(entry.Category, Category, StringComparison.Ordinal));

		public CapturedLogEntry Single(int eventId) => McpEntries.Should().ContainSingle(entry => entry.EventId == eventId).Subject;

		public int Count(int eventId) => McpEntries.Count(entry => entry.EventId == eventId);

		public int Count(LogLevel level) => McpEntries.Count(entry => entry.Level == level);

		public void Dispose()
		{
		}
	}

	private sealed class CapturingLogger(string categoryName, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			entries.Enqueue(new CapturedLogEntry(categoryName, logLevel, eventId.Id, formatter(state, exception), exception));
	}

	private sealed record CapturedLogEntry(string Category, LogLevel Level, int EventId, string Message, Exception? Exception);
}
