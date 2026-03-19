using System.IO.Pipelines;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

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
	[Description("Exception during routing rebuild does not crash the server.")]
	public void When_RebuildThrows_Then_ServerContinuesWithStaleRoutes()
	{
		var fakeTime = new FakeTimeProvider();
		using var fixture = CreateServerFixture(fakeTime);

		// Verify initial state — tool is available.
		var tools = SyncWait(fixture.Client.ListToolsAsync().AsTask());
		tools.Should().ContainSingle(t => string.Equals(t.Name, "initial", StringComparison.Ordinal));

		// Add a command filter that will throw during the next rebuild.
		fixture.Options.CommandFilter = _ => throw new InvalidOperationException("Simulated rebuild failure");
		fixture.App.Core.InvalidateRouting();

		// Advance time to trigger the rebuild — should not crash.
		fakeTime.Advance(TimeSpan.FromMilliseconds(150));

		// Server should still respond — existing tools remain.
		var toolsAfter = SyncWait(fixture.Client.ListToolsAsync().AsTask());
		toolsAfter.Should().NotBeEmpty("server should continue with stale routes after rebuild failure");
	}

	// ── Sync-over-async helper ──────────────────────────────────────────
	// FakeTimeProvider requires synchronous test control — timer callbacks
	// fire during Advance(), so the test method must be sync. Async calls
	// (MCP client) are awaited via bounded Wait() to fail fast on deadlock.

#pragma warning disable VSTHRD002 // Intentional sync-over-async for deterministic time tests.
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

	private static ServerFixture CreateServerFixture(TimeProvider timeProvider)
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("initial", () => "ok");

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
				serverToClient.Reader.AsStream())));

		return new ServerFixture(app, options, client, cts, clientToServer, serverToClient, serverTask);
	}

	private sealed class ServerFixture(
		ReplApp app, ReplMcpServerOptions options, McpClient client,
		CancellationTokenSource cts, Pipe c2s, Pipe s2c, Task serverTask) : IDisposable
	{
		public ReplApp App => app;
		public ReplMcpServerOptions Options => options;
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
}
