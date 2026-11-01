using AwesomeAssertions;
using HostingRemoteSample;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Repl.Remote;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_HostingHealth
{
	[TestMethod]
	[DataRow(false, false, false, false, 1, DisplayName = "Not started")]
	[DataRow(true, false, false, false, 0, DisplayName = "Ready")]
	[DataRow(true, false, true, false, 1, DisplayName = "Maintenance blocks readiness")]
	[DataRow(true, false, true, true, 0, DisplayName = "Maintenance permits liveness")]
	[DataRow(true, true, false, false, 1, DisplayName = "Stopping readiness")]
	[DataRow(true, true, false, true, 1, DisplayName = "Stopping liveness")]
	[Description("Readiness follows host startup, maintenance and shutdown; liveness ignores maintenance while still refusing a stopping host.")]
	public async Task When_HealthIsProbed_Then_ItReportsTheRunningHostsState(bool started, bool stopping, bool maintenance, bool liveness, int expectedCode)
	{
		using var lifetime = new TestHostLifetime(started, stopping);
		var settings = new InMemorySettingsService();
		settings.Set("maintenance", maintenance ? "on" : "off");
		var app = ReplApp.Create(services => services.AddSingleton<IHostApplicationLifetime>(lifetime));
		app.MapModule(new RemoteModule(settings, new InMemoryMessageBus(), new SessionTracker(), lifetime, NullLogger<RemoteModule>.Instance));
		await using var backend = new ReplAppRemoteBackend(app);

		var result = await backend.ExecuteAsync(new() { Arguments = liveness ? ["health", "--liveness"] : ["health"] }, CancellationToken.None);

		result.ExitCode.Should().Be(expectedCode);
		result.Result.Should().NotBeNull();
		result.Result.Value.GetProperty("status").GetString().Should().Be(liveness ? (expectedCode == 0 ? "alive" : "notAlive") : (expectedCode == 0 ? "ready" : "notReady"));
	}

	private sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
	{
		private readonly CancellationTokenSource _started = new();
		private readonly CancellationTokenSource _stopping = new();
		public TestHostLifetime(bool started, bool stopping)
		{
#pragma warning disable MA0045 // Prepare lifetime state synchronously before any callbacks are registered.
			if (started) { _started.Cancel(); }
			if (stopping) { _stopping.Cancel(); }
#pragma warning restore MA0045
		}
		public CancellationToken ApplicationStarted => _started.Token;
		public CancellationToken ApplicationStopping => _stopping.Token;
		public CancellationToken ApplicationStopped => CancellationToken.None;
		public void StopApplication() => _stopping.Cancel();
		public void Dispose() { _started.Dispose(); _stopping.Dispose(); }
	}
}
