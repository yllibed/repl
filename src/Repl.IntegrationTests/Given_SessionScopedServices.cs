using Microsoft.Extensions.DependencyInjection;
using Repl.Testing;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_SessionScopedServices
{
	private sealed class ScopedProbe
	{
		public Guid Id { get; } = Guid.NewGuid();
	}

	private sealed class TransientProbe
	{
		public Guid Id { get; } = Guid.NewGuid();
	}

	private sealed class DisposableProbe(List<Guid> disposed) : IDisposable
	{
		public Guid Id { get; } = Guid.NewGuid();

		public void Dispose() => disposed.Add(Id);
	}

	private static ReplTestHost CreateProbeHost(List<Guid>? disposedTracker = null) =>
		ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create(services =>
			{
				services.AddScoped<ScopedProbe>();
				services.AddTransient<TransientProbe>();
				services.AddScoped(_ => new DisposableProbe(disposedTracker ?? []));
			}).UseDefaultInteractive();
			app.Map("scoped", (ScopedProbe probe) => probe.Id.ToString());
			app.Map("transient", (TransientProbe probe) => probe.Id.ToString());
			app.Map("disposable", (DisposableProbe probe) => probe.Id.ToString());
			return app;
		});

	private static async Task<string> RunAndCaptureAsync(ReplSessionHandle session, string command)
	{
		var result = await session.RunCommandAsync($"{command} --no-logo").ConfigureAwait(false);
		result.ExitCode.Should().Be(0, "command output was: {0}", result.OutputText);
		return result.OutputText.Trim();
	}

	[TestMethod]
	[Description("Guards the documented Scoped lifetime for hosted sessions: two distinct hosted sessions must resolve two distinct Scoped instances — sharing one instance across sessions leaks per-user state (auth context, carts) between concurrent clients.")]
	public async Task When_TwoHostedSessionsResolveScopedService_Then_InstancesDiffer()
	{
		await using var host = CreateProbeHost();
		await using var sessionA = await host.OpenSessionAsync();
		await using var sessionB = await host.OpenSessionAsync();

		var idA = await RunAndCaptureAsync(sessionA, "scoped");
		var idB = await RunAndCaptureAsync(sessionB, "scoped");

		idA.Should().NotBe(idB);
	}

	[TestMethod]
	[Description("Guards the session-scope boundary from the other side: two commands within the SAME hosted session share the same Scoped instance — the scope is per session, not per command invocation.")]
	public async Task When_SameSessionRunsTwoCommands_Then_ScopedInstanceIsShared()
	{
		await using var host = CreateProbeHost();
		await using var session = await host.OpenSessionAsync();

		var first = await RunAndCaptureAsync(session, "scoped");
		var second = await RunAndCaptureAsync(session, "scoped");

		first.Should().Be(second);
	}

	[TestMethod]
	[Description("Guards Transient semantics under the session scope: every resolution yields a fresh instance, including across commands of the same session.")]
	public async Task When_TransientResolvedInTwoCommands_Then_InstancesDiffer()
	{
		await using var host = CreateProbeHost();
		await using var session = await host.OpenSessionAsync();

		var first = await RunAndCaptureAsync(session, "transient");
		var second = await RunAndCaptureAsync(session, "transient");

		first.Should().NotBe(second);
	}

	[TestMethod]
	[Description("Guards scoped-disposable lifetime: a Scoped IDisposable resolved during a hosted session is disposed when that session ends, not deferred to app shutdown — session resources (connections, per-user stores) must not accumulate for the host process lifetime.")]
	public async Task When_SessionEnds_Then_ScopedDisposablesAreDisposed()
	{
		var disposed = new List<Guid>();
		await using var host = CreateProbeHost(disposed);

		string id;
		{
			await using var session = await host.OpenSessionAsync();
			id = await RunAndCaptureAsync(session, "disposable");
			disposed.Should().NotContain(Guid.Parse(id));
		}

		disposed.Should().Contain(Guid.Parse(id));
	}
}
