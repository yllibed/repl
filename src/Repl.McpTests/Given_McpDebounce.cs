using System.IO.Pipelines;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;
using Repl.Parameters;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpDebounce
{
	[TestMethod]
	[Description("Multiple rapid routing invalidations are coalesced into a single rebuild after debounce delay.")]
	public void When_MultipleInvalidations_Then_SingleRebuildAfterDebounce()
	{
		var fakeTime = new FakeTimeProvider();
		using var fixture = CreateServerFixture(fakeTime);

		// Verify initial state.
		var tools = SyncWait(fixture.Client.ListToolsAsync().AsTask());
		tools.Should().ContainSingle(t => string.Equals(t.Name, "initial", StringComparison.Ordinal));

		// Fire 5 rapid invalidations — debounce timer resets each time, no rebuild yet.
		for (var i = 0; i < 5; i++)
		{
			fixture.App.Core.InvalidateRouting();
		}

		// Add a new command so the rebuild produces a visible change.
		fixture.App.Map("added-after", () => "new");
		fixture.App.Core.InvalidateRouting();

		// FakeTimeProvider.Advance() fires timer callbacks synchronously in the
		// calling thread — no Thread.Sleep or polling needed.
		fakeTime.Advance(TimeSpan.FromMilliseconds(150));

		// Verify the rebuild happened — new tool should be visible.
		var updatedTools = SyncWait(fixture.Client.ListToolsAsync().AsTask());
		updatedTools.Should().Contain(
			t => string.Equals(t.Name, "added-after", StringComparison.Ordinal),
			"debounce should have triggered a rebuild that includes the new command");
	}

	[TestMethod]
	[Description("Exception during routing rebuild does not crash an initialize-era session, which keeps serving its previous catalog. The fallback is bounded to that era on purpose: 2026-07-28 forbids the advertised set from varying per connection, and answering one connection from its own cache is precisely that variance, so a modern request fails closed instead.")]
	public void When_RebuildThrows_Then_ALegacySessionContinuesWithStaleRoutes()
	{
		var fakeTime = new FakeTimeProvider();
		using var fixture = CreateServerFixture(fakeTime, BuildLegacyClientOptions());

		// Verify initial state — tool is available.
		var tools = SyncWait(fixture.Client.ListToolsAsync().AsTask());
		tools.Should().ContainSingle(t => string.Equals(t.Name, "initial", StringComparison.Ordinal));

		// Add a route that only a successful refresh can reveal, then make the first rebuild fail.
		fixture.App.Map("added-after", () => "new");
		fixture.Options.CommandFilter = _ => throw new InvalidOperationException("Simulated rebuild failure");
		fixture.App.Core.InvalidateRouting();

		// Advance time to trigger the rebuild — should not crash.
		fakeTime.Advance(TimeSpan.FromMilliseconds(150));

		// Server should still respond with the previous snapshot while the failure is active.
		var staleTools = SyncWait(fixture.Client.ListToolsAsync().AsTask());
		staleTools.Should().ContainSingle(tool => string.Equals(tool.Name, "initial", StringComparison.Ordinal));

		// Clearing a transient failure must let the same invalidated version retry without another mutation.
		fixture.Options.CommandFilter = null;
		var recoveredTools = SyncWait(fixture.Client.ListToolsAsync().AsTask());
		recoveredTools.Should().Contain(tool => string.Equals(tool.Name, "added-after", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Regression guard: an availability fallback must not be lost because the failure arrived as a cancellation nobody asked for. The roots fetch a legacy projection awaits runs on its own budget, independent of the caller's token, so the budget expiring surfaces as an OperationCanceledException while the request's own token is still live — and the unfiltered cancellation arm sat above the fallback, rethrowing past a catalog this connection had been serving a moment earlier. Cancellation is told apart by who asked for it, not by the exception's type, which is the rule the roots service and the App resource path already apply.")]
	public void When_AProjectionIsCancelledByNobody_Then_ALegacySessionKeepsServingThePreviousCatalog()
	{
		var fakeTime = new FakeTimeProvider();
		using var fixture = CreateServerFixture(fakeTime, BuildLegacyClientOptions());

		SyncWait(fixture.Client.ListToolsAsync().AsTask())
			.Should().ContainSingle(tool => string.Equals(tool.Name, "initial", StringComparison.Ordinal));

		// A foreign, already-cancelled token: the shape the roots budget produces when it expires, on a
		// caller whose own token was never touched.
		fixture.Options.CommandFilter = _ => throw new OperationCanceledException(new CancellationToken(canceled: true));
		fixture.App.Core.InvalidateRouting();
		fakeTime.Advance(TimeSpan.FromMilliseconds(150));

		var stale = SyncWait(fixture.Client.ListToolsAsync().AsTask());

		stale.Should().ContainSingle(
			tool => string.Equals(tool.Name, "initial", StringComparison.Ordinal),
			because: "this connection had a serve-able catalog and nobody withdrew the request");
	}

	[TestMethod]
	[Description("Regression guard: verifies the availability fallback keeps applying after a visibility retraction. Republishing a served-but-stale snapshot used to overwrite the version it was built at with a zero sentinel, so the retraction watermark was compared against zero and read as older than every retraction ever published. The FIRST failed projection still served the previous catalog and the second surfaced the error instead — a catalog that had been serving a moment earlier became unreachable for as long as the failure lasted. Hiding a command is the retraction: without one the watermark stays at zero, where the sentinel happened to compare equal and the defect is invisible. Initialize-era, because that is the only era the availability fallback applies to.")]
	public void When_ProjectionKeepsFailingAfterARetraction_Then_ALegacySessionKeepsServingThePreviousCatalog()
	{
		var fakeTime = new FakeTimeProvider();
		using var fixture = CreateServerFixture(fakeTime, BuildLegacyClientOptions());
		var extra = fixture.App.Map("extra", static () => "x");

		SyncWait(fixture.Client.ListToolsAsync().AsTask())
			.Should().Contain(tool => string.Equals(tool.Name, "extra", StringComparison.Ordinal));

		// Hiding a mapped command publishes a visibility retraction, which moves the watermark the
		// availability fallback compares its cached snapshot against.
		extra.Hidden();
		SyncWait(fixture.Client.ListToolsAsync().AsTask())
			.Should().NotContain(tool => string.Equals(tool.Name, "extra", StringComparison.Ordinal));

		// From here every projection throws.
		fixture.Options.CommandFilter = _ => throw new InvalidOperationException("Simulated rebuild failure");
		fixture.App.Core.InvalidateRouting();
		fakeTime.Advance(TimeSpan.FromMilliseconds(150));

		var first = SyncWait(fixture.Client.ListToolsAsync().AsTask());
		var second = SyncWait(fixture.Client.ListToolsAsync().AsTask());

		first.Should().ContainSingle(tool => string.Equals(tool.Name, "initial", StringComparison.Ordinal));
		second.Should().ContainSingle(
			tool => string.Equals(tool.Name, "initial", StringComparison.Ordinal),
			because: "the second read must not lose the fallback the first one just used");
	}

	[TestMethod]
	[Description("Pausing immediately before invalidation publication leaves readers on the complete old version/watermark pair; releasing publication exposes the complete new pair atomically.")]
	public void When_VisibilityRetractionPublicationIsPaused_Then_ReaderObservesOnlyCompleteStates()
	{
		var initial = new McpServerHandler.SnapshotVersionState(
			Version: 7,
			LastVisibilityRetractionVersion: 3);
		var holder = new SnapshotStateHolder(initial);
		using var publicationReady = new ManualResetEventSlim(initialState: false);
		using var releasePublication = new ManualResetEventSlim(initialState: false);

		var publication = Task.Run(() => McpServerHandler.PublishSnapshotInvalidation(
			ref holder.State,
			isVisibilityRetraction: true,
			beforePublish: candidate =>
			{
				candidate.Version.Should().Be(8);
				candidate.LastVisibilityRetractionVersion.Should().Be(8);
				publicationReady.Set();
				releasePublication.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
			}));

		publicationReady.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
		Volatile.Read(ref holder.State).Should().BeSameAs(initial);

		releasePublication.Set();
		SyncWait(publication);
		var published = Volatile.Read(ref holder.State);
		published.Version.Should().Be(8);
		published.LastVisibilityRetractionVersion.Should().Be(8);
	}

	[TestMethod]
	[Description("If an option is hidden while a successful MCP snapshot projection is in flight, that request retries against the newer visibility version instead of returning the stale schema it already constructed.")]
	public void When_VisibilityRetractionOccursDuringSuccessfulBuild_Then_InFlightRequestRetries()
	{
		var fakeTime = new FakeTimeProvider();
		using var fixture = CreateServerFixture(fakeTime);
		var initial = SyncWait(fixture.Client.ListToolsAsync().AsTask()).Single();
		initial.JsonSchema.GetProperty("properties").TryGetProperty("tenant", out _).Should().BeTrue();

		using var projectionEntered = new ManualResetEventSlim(initialState: false);
		using var releaseProjection = new ManualResetEventSlim(initialState: false);
		fixture.Options.CommandFilter = _ =>
		{
			projectionEntered.Set();
			return releaseProjection.Wait(TimeSpan.FromSeconds(10));
		};
		fixture.App.Core.InvalidateRouting();

		var refresh = fixture.Client.ListToolsAsync().AsTask();
		projectionEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the test must interleave the visibility change with snapshot projection");
		fixture.InitialCommand.WithOption("tenant", static option => option.Hidden());
		releaseProjection.Set();

		var rebuilt = SyncWait(refresh).Single();
		rebuilt.JsonSchema.GetProperty("properties").TryGetProperty("tenant", out _).Should().BeFalse();
	}

	[TestMethod]
	[Description("A visibility retraction fails closed while snapshot projection is broken, rather than serving the previous MCP schema that still exposes the hidden option; a later successful rebuild clears the fail-closed state.")]
	public void When_VisibilityRetractionRefreshFails_Then_StaleSnapshotIsNotServed()
	{
		var fakeTime = new FakeTimeProvider();
		using var fixture = CreateServerFixture(fakeTime);

		var initial = SyncWait(fixture.Client.ListToolsAsync().AsTask()).Single();
		initial.JsonSchema.GetProperty("properties").TryGetProperty("tenant", out _).Should().BeTrue();

		fixture.Options.CommandFilter = _ => throw new InvalidOperationException("Simulated rebuild failure");
		fixture.InitialCommand.WithOption("tenant", static option => option.Hidden());
		fakeTime.Advance(TimeSpan.FromMilliseconds(150));

		Action staleRead = () => _ = SyncWait(fixture.Client.ListToolsAsync().AsTask());
		staleRead.Should().Throw<Exception>("a stale schema would still advertise and accept the explicitly hidden option");

		fixture.Options.CommandFilter = null;
		var recovered = SyncWait(fixture.Client.ListToolsAsync().AsTask()).Single();
		recovered.JsonSchema.GetProperty("properties").TryGetProperty("tenant", out _).Should().BeFalse();
	}

	// ── Sync-over-async helper ──────────────────────────────────────────
	// FakeTimeProvider requires synchronous test control — timer callbacks
	// fire during Advance(), so the test method must be sync. Async calls
	// (MCP client) are awaited via bounded Wait() to fail fast on deadlock.

#pragma warning disable VSTHRD002 // Intentional sync-over-async for deterministic time tests.
	/// <summary>A client that negotiates the initialize era, where the catalog is session state.</summary>
	private static McpClientOptions BuildLegacyClientOptions() =>
		new() { ProtocolVersion = McpProtocolRevisions.LastWithSessions };

	private static T SyncWait<T>(Task<T> task)
	{
		if (!task.Wait(TimeSpan.FromSeconds(10)))
		{
			throw new TimeoutException("Task did not complete — possible deadlock.");
		}

		return task.GetAwaiter().GetResult();
	}
#pragma warning restore VSTHRD002

	// ── Fixture ─────────────────────────────────────────────────────────

	private static ServerFixture CreateServerFixture(
		TimeProvider timeProvider,
		McpClientOptions? clientOptions = null)
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		var initialCommand = app.Map(
			"initial",
			static string ([ReplOption] string? tenant = null) => tenant ?? "ok");

		var clientToServer = new Pipe();
		var serverToClient = new Pipe();
		var options = new ReplMcpServerOptions
		{
			TransportFactory = (name, _) => new StreamServerTransport(
				clientToServer.Reader.AsStream(),
				serverToClient.Writer.AsStream(), name),
		};
		var services = new FakeServiceProvider(timeProvider);
		var handler = new McpServerHandler(app.Core, options, services);
		var cts = new CancellationTokenSource();

		// RunAsync subscribes to RoutingInvalidated and blocks on the transport.
		var serverTask = handler.RunAsync(new NullIoContext(), cts.Token);
		var client = SyncWait(McpClient.CreateAsync(
			new StreamClientTransport(
				clientToServer.Writer.AsStream(),
				serverToClient.Reader.AsStream()),
			clientOptions));

		return new ServerFixture(app, options, initialCommand, client, cts, clientToServer, serverToClient, serverTask);
	}

	private sealed class ServerFixture(
		ReplApp app, ReplMcpServerOptions options, CommandBuilder initialCommand, McpClient client,
		CancellationTokenSource cts, Pipe c2s, Pipe s2c, Task serverTask) : IDisposable
	{
		public ReplApp App => app;
		public ReplMcpServerOptions Options => options;
		public CommandBuilder InitialCommand => initialCommand;
		public McpClient Client => client;

#pragma warning disable VSTHRD002
		public void Dispose()
		{
			client.DisposeAsync().AsTask().GetAwaiter().GetResult();
			cts.Cancel();
			c2s.Writer.Complete();
			s2c.Writer.Complete();
			try { serverTask.GetAwaiter().GetResult(); }
			catch (OperationCanceledException) { /* Expected: server RunAsync cancelled during shutdown. */ }
			cts.Dispose();
		}
#pragma warning restore VSTHRD002
	}

	private sealed class FakeServiceProvider(TimeProvider timeProvider) : IServiceProvider
	{
		public object? GetService(Type serviceType) =>
			serviceType == typeof(TimeProvider) ? timeProvider : null;
	}

	private sealed class NullIoContext : IReplIoContext
	{
		public TextReader Input => TextReader.Null;
		public TextWriter Output => TextWriter.Null;
		public TextWriter Error => TextWriter.Null;
		public bool IsHostedSession => false;
		public string? SessionId => null;
	}
	private sealed class SnapshotStateHolder(McpServerHandler.SnapshotVersionState state)
	{
		public McpServerHandler.SnapshotVersionState State = state;
	}

}
