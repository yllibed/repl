using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

	// ONE app for every session this host opens. ReplTestHost invokes the factory per session, so
	// returning a fresh ReplApp would give each session its own container — and then two sessions
	// resolve distinct instances whatever lifetime is registered, which makes a cross-session
	// isolation assertion pass on a full revert of the feature it claims to guard.
	private static ReplTestHost CreateProbeHost(List<Guid>? disposedTracker = null)
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

		return ReplTestHost.Create(() => app);
	}

	private static async Task<string> RunAndCaptureAsync(ReplSessionHandle session, string command)
	{
		var result = await session.RunCommandAsync($"{command} --no-logo").ConfigureAwait(false);
		result.ExitCode.Should().Be(0, "command output was: {0}", result.OutputText);
		return result.OutputText.Trim();
	}

	[TestMethod]
	[Description("Guards the core per-run session scope on ONE shared app: two Run* calls against the same ReplApp (the exact shape of two hosted connections sharing one app) must resolve two distinct Scoped instances from the app's own root provider — this exercises the scope-creation branch itself, independently of Repl.Testing's session plumbing.")]
	public void When_TwoRunsShareOneApp_Then_ScopedInstancesDiffer()
	{
		var sut = ReplApp.Create(services => services.AddScoped<ScopedProbe>());
		sut.Map("scoped", (ScopedProbe probe) => probe.Id.ToString());

		var first = ConsoleCaptureHelper.Capture(() => sut.Run(["scoped", "--no-logo"]));
		var second = ConsoleCaptureHelper.Capture(() => sut.Run(["scoped", "--no-logo"]));

		first.ExitCode.Should().Be(0);
		second.ExitCode.Should().Be(0);
		first.Text.Trim().Should().NotBe(second.Text.Trim());
	}

	[TestMethod]
	[Description("Regression guard: verifies an undefined SessionScopeBehavior is rejected instead of falling through a negative test into PerRun, where it would silently nest a scope inside a caller who may have asked to own it — hiding the caller's own scoped instances rather than sharing them, the same class of risk ProcessSignalHandling's own undefined-value rejection guards.")]
	public async Task When_TheSessionScopeIsUndefined_Then_TheRunIsRejected()
	{
		var sut = ReplApp.Create(services => services.AddScoped<ScopedProbe>());
		sut.Map("scoped", (ScopedProbe probe) => probe.Id.ToString());

		Func<Task> act = () => sut.RunAsync(
				["scoped", "--no-logo"],
				new ReplRunOptions { SessionScope = (SessionScopeBehavior)42 })
			.AsTask();

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Guards the CallerOwned opt-out: a caller whose provider already represents the session scope (Blazor circuit, per-request scope, multi-run session owners) must keep its own Scoped instances across runs — the entry points must not open a nested sibling scope that would hide them.")]
	public async Task When_CallerOwnsTheSessionScope_Then_RunsShareTheCallerScopedInstances()
	{
		var sut = ReplApp.Create(services => services.AddScoped<ScopedProbe>());
		sut.Map("scoped", (ScopedProbe probe) => probe.Id.ToString());
		var scopeFactory = sut.Services.GetRequiredService<IServiceScopeFactory>();
		var callerScope = scopeFactory.CreateAsyncScope();
		await using (callerScope.ConfigureAwait(false))
		{
			var options = new ReplRunOptions { SessionScope = SessionScopeBehavior.CallerOwned };

			var first = ConsoleCaptureHelper.Capture(
				() => sut.Run(["scoped", "--no-logo"], callerScope.ServiceProvider, options));
			var second = ConsoleCaptureHelper.Capture(
				() => sut.Run(["scoped", "--no-logo"], callerScope.ServiceProvider, options));

			first.ExitCode.Should().Be(0);
			first.Text.Trim().Should().Be(second.Text.Trim());
		}
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

		disposed.Should().ContainSingle(guid => guid == Guid.Parse(id));
	}

	private sealed class ScopeObservingHostedService(IServiceProvider services, List<Guid> observed) : IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken)
		{
			observed.Add(services.GetRequiredService<ScopedProbe>().Id);
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}

	[TestMethod]
	[Description("Guards the scope boundary on the hosted-service path: the run pipeline is wrapped in the session scope but HostedServiceLifecycleCoordinator.StartAsync is not, so a hosted service resolves from the unscoped provider and must NOT see the command's Scoped instance. Placing the scope around the whole hosted branch instead would tie app-level services to a session lifetime.")]
	public void When_HostedServicesRun_Then_TheyStartOutsideTheSessionScope()
	{
		var observed = new List<Guid>();
		var sut = ReplApp.Create(services =>
		{
			services.AddScoped<ScopedProbe>();
			services.AddSingleton(observed);
			// Scoped, not singleton: a singleton is always constructed in the root and always injected
			// the root provider, so it reports the same instance wherever the scope boundary sits and
			// the assertion below could never fail.
			services.AddScoped<IHostedService, ScopeObservingHostedService>();
		});
		sut.Map("scoped", (ScopedProbe probe) => probe.Id.ToString());
		var options = new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head };

		var first = ConsoleCaptureHelper.Capture(() => sut.Run(["scoped", "--no-logo"], options));
		var second = ConsoleCaptureHelper.Capture(() => sut.Run(["scoped", "--no-logo"], options));

		first.ExitCode.Should().Be(0, "run output was: {0}", first.Text);
		second.ExitCode.Should().Be(0, "run output was: {0}", second.Text);

		var firstCommandId = first.Text.Trim();
		var secondCommandId = second.Text.Trim();
		firstCommandId.Should().NotBe(secondCommandId, "each hosted run owns its session scope");

		observed.Should().HaveCount(2);
		observed.Select(id => id.ToString()).Should().NotContain(firstCommandId);
		observed.Select(id => id.ToString()).Should().NotContain(secondCommandId);
	}

	[TestMethod]
	[Description("Guards the fourth run path: ProcessSignalHandlingMode.Automatic reaches the core through RunOutcomeAsync directly, bypassing RunWithServicesAsync. Scoping at that bypassed method would leave standalone signal-owning runs sharing one Scoped instance across runs.")]
	public void When_AStandaloneRunOwnsProcessSignals_Then_ItStillGetsItsOwnSessionScope()
	{
		var sut = ReplApp.Create(services => services.AddScoped<ScopedProbe>());
		sut.Map("scoped", (ScopedProbe probe) => probe.Id.ToString());
		var options = new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.Automatic };

		var first = ConsoleCaptureHelper.Capture(() => sut.Run(["scoped", "--no-logo"], options));
		var second = ConsoleCaptureHelper.Capture(() => sut.Run(["scoped", "--no-logo"], options));

		first.ExitCode.Should().Be(0, "run output was: {0}", first.Text);
		second.ExitCode.Should().Be(0, "run output was: {0}", second.Text);
		first.Text.Trim().Should().NotBe(second.Text.Trim());
	}

	[TestMethod]
	[Description("Regression guard for the session command gate: taking the gate before disposing the session scope must not let a still-running command block disposal indefinitely. DisposeAsync waits on the gate, so an unbounded wait turns an orphaned command into a hung await using, and disposing the semaphore while a caller is queued on it strands that caller forever.")]
	public async Task When_ACommandIsStillRunning_Then_DisposingTheSessionDoesNotHang()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var host = ReplTestHost.Create(
			() =>
			{
				var app = ReplApp.Create().UseDefaultInteractive();
				app.Map("block", async () =>
				{
					started.TrySetResult();

					// VSTHRD003: the test owns both sources; this command exists to hold the session command
					// gate open until the test releases it.
#pragma warning disable VSTHRD003
					await release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
					return "done";
				});
				return app;
			},
			options => options.CommandTimeout = TimeSpan.FromMinutes(5));

		var session = await host.OpenSessionAsync();
		var running = session.RunCommandAsync("block --no-logo").AsTask();
		await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

		var dispose = session.DisposeAsync().AsTask();
		var disposedInTime = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5))) == dispose;

		release.TrySetResult();
		var completed = await running.WaitAsync(TimeSpan.FromSeconds(30));
		await dispose.WaitAsync(TimeSpan.FromSeconds(30));

		disposedInTime.Should().BeTrue(
			"disposing a session must not wait unbounded on a command that is still running");
		completed.ExitCode.Should().Be(
			0,
			"disposal must not tear the session scope out from under the command that is still using it");
	}

	[TestMethod]
	[Description("Guards the deferred half of disposal: DisposeAsync returning early while a command is still running must not be the ONLY disposal attempt. The command's own finally must finish the job once it completes, so a Scoped disposable it resolved is still released deterministically rather than left for the process to reclaim.")]
	public async Task When_SessionIsDisposedWhileACommandRuns_Then_ScopedDisposablesAreStillReleased()
	{
		var disposed = new List<Guid>();
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var host = ReplTestHost.Create(
			() =>
			{
				var app = ReplApp.Create(services =>
					services.AddScoped(_ => new DisposableProbe(disposed))).UseDefaultInteractive();
				app.Map("block", async (DisposableProbe probe) =>
				{
					started.TrySetResult();

#pragma warning disable VSTHRD003
					await release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
					return probe.Id.ToString();
				});
				return app;
			},
			options => options.CommandTimeout = TimeSpan.FromMinutes(5));

		var session = await host.OpenSessionAsync();
		var running = session.RunCommandAsync("block --no-logo").AsTask();
		await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

		var dispose = session.DisposeAsync().AsTask();
		await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)));
		disposed.Should().BeEmpty("disposal must not race the command that is still using the scope");

		release.TrySetResult();
		var completed = await running.WaitAsync(TimeSpan.FromSeconds(30));
		await dispose.WaitAsync(TimeSpan.FromSeconds(30));

		completed.ExitCode.Should().Be(0, "run output was: {0}", completed.OutputText);
		disposed.Should().ContainSingle(
			"the deferred command must finish the disposal DisposeAsync could not do while it was running");
	}

	[TestMethod]
	[Description("Guards the framework's own per-session service: IReplSessionState is documented as a per-session state container, so two sessions of one app must not read each other's writes. Registered as a singleton it was one process-wide bag shared by every concurrent Telnet, WebSocket and MCP client.")]
	public void When_TwoRunsShareOneApp_Then_SessionStateIsNotShared()
	{
		var sut = ReplApp.Create();
		sut.Map("remember {value}", (IReplSessionState state, string value) =>
		{
			state.Set("carried", value);
			return "stored";
		});
		sut.Map("recall", (IReplSessionState state) => state.Get<string>("carried") ?? "(none)");

		var write = ConsoleCaptureHelper.Capture(() => sut.Run(["remember", "alpha", "--no-logo"]));
		var read = ConsoleCaptureHelper.Capture(() => sut.Run(["recall", "--no-logo"]));

		write.ExitCode.Should().Be(0, "run output was: {0}", write.Text);
		read.ExitCode.Should().Be(0, "run output was: {0}", read.Text);
		read.Text.Trim().Should().Be("(none)", "a later session must not read an earlier session's state");
	}

	private sealed class SessionStateCapturingSingleton(IReplSessionState state)
	{
		public IReplSessionState Captured { get; } = state;
	}

	[TestMethod]
	[Description("Documents the known consequence of making IReplSessionState scoped: a SINGLETON that injects it captures, for the life of the app, whichever session scope first resolved it, silently, because the provider is built without ValidateScopes. This test exists so the trap is discoverable and so enabling scope validation later is a deliberate change rather than an accident. Inject IReplSessionState into the handler, or register the holder scoped.")]
	public void When_ASingletonInjectsSessionState_Then_ItCapturesTheFirstSessionInstance()
	{
		var sut = ReplApp.Create(services => services.AddSingleton<SessionStateCapturingSingleton>());

		// Resolves the singleton inside the FIRST session, which is what pins that session scope to it.
		sut.Map("write {value}", (SessionStateCapturingSingleton holder, string value) =>
		{
			holder.Captured.Set("carried", value);
			return "stored";
		});
		sut.Map(
			"captured",
			(SessionStateCapturingSingleton holder) => holder.Captured.Get<string>("carried") ?? "(none)");

		var write = ConsoleCaptureHelper.Capture(() => sut.Run(["write", "alpha", "--no-logo"]));
		var captured = ConsoleCaptureHelper.Capture(() => sut.Run(["captured", "--no-logo"]));

		write.ExitCode.Should().Be(0, "run output was: {0}", write.Text);
		captured.ExitCode.Should().Be(0, "run output was: {0}", captured.Text);
		captured.Text.Trim().Should().Be(
			"alpha",
			"a singleton holds the scope it was first resolved in, so it still sees that session");
	}

	private sealed class SingletonProbe : IDisposable
	{
		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	[TestMethod]
	[Description("The host, not only the session, must release what a session's DI scope pulled its app's root provider into building: a singleton registered on the app is constructed once the first session's scope resolves it, and nothing but the host disposing that root provider ever releases it. Reproduces the leak by resolving a disposable singleton, disposing the session, then the host, and asserting it is disposed only at that second point — never left for the process to reclaim.")]
	public async Task When_TheHostIsDisposed_Then_TheAppRootProviderIsDisposedToo()
	{
		var probe = new SingletonProbe();
		await using var host = ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create(services => services.AddSingleton(_ => probe)).UseDefaultInteractive();
			app.Map("touch", (SingletonProbe p) => p.Disposed.ToString());
			return app;
		});

		var session = await host.OpenSessionAsync();
		var result = await session.RunCommandAsync("touch --no-logo");
		result.ExitCode.Should().Be(0, "run output was: {0}", result.OutputText);

		await session.DisposeAsync();
		probe.Disposed.Should().BeFalse(
			"disposing the session releases its DI scope, not the app's root provider it descends from");

		await host.DisposeAsync();
		probe.Disposed.Should().BeTrue(
			"the host built the app's root provider to open this session and must release it at its own disposal");
	}

	[TestMethod]
	[Description("Guards against a double-dispose crash on the shared-app shape this file's own CreateProbeHost helper relies on: several sessions opened against ONE ReplApp must have that app's root provider disposed exactly once when the host is disposed, not once per session.")]
	public async Task When_SeveralSessionsShareOneApp_Then_TheHostDisposesItsRootProviderOnce()
	{
		var probe = new SingletonProbe();
		var app = ReplApp.Create(services => services.AddSingleton(_ => probe)).UseDefaultInteractive();
		app.Map("touch", (SingletonProbe p) => p.Disposed.ToString());
		await using var host = ReplTestHost.Create(() => app);

		await using var sessionA = await host.OpenSessionAsync();
		await using var sessionB = await host.OpenSessionAsync();
		(await sessionA.RunCommandAsync("touch --no-logo")).ExitCode.Should().Be(0);
		(await sessionB.RunCommandAsync("touch --no-logo")).ExitCode.Should().Be(0);

		var dispose = async () => await host.DisposeAsync().ConfigureAwait(false);

		await dispose.Should().NotThrowAsync("one shared app must be disposed once, not once per session")
			.ConfigureAwait(false);
		probe.Disposed.Should().BeTrue();
	}

	// Deliberately not built via ServiceCollection.BuildServiceProvider(): the point of this provider is
	// that it supplies no IServiceScopeFactory, which is exactly the case RunInSessionScopeAsync degrades
	// for — a caller-supplied provider that was never given a scope, as opposed to CallerOwned, where the
	// caller opted out on purpose.
	private sealed class ScopeLessServiceProvider(ReplApp app) : IServiceProvider
	{
		public object? GetService(Type serviceType) =>
			serviceType == typeof(IServiceScopeFactory) ? null : app.Services.GetService(serviceType);
	}

	[TestMethod]
	[Description("Issue #70's own symptom must not reappear silently: a caller-supplied provider with no IServiceScopeFactory makes every session share one Scoped instance, exactly what SessionScope exists to prevent. Unlike SessionScopeBehavior.CallerOwned, which is a deliberate and silent opt-out, this is a provider nobody chose to leave unscoped, so it must say so.")]
	public async Task When_ProviderHasNoScopeFactory_Then_ARunTimeDiagnosticNamesTheDefect()
	{
		var sut = ReplApp.Create(services => services.AddScoped<ScopedProbe>());
		sut.Map("scoped", (ScopedProbe probe) => probe.Id.ToString());
		var services = new ScopeLessServiceProvider(sut);
		using var diagnostics = new StringWriter();
		using var session = ReplSessionIO.SetSession(TextWriter.Null, TextReader.Null, error: diagnostics);

		var exitCode = await sut.RunAsync(["scoped", "--no-logo"], services).ConfigureAwait(false);

		exitCode.Should().Be(0);
		diagnostics.ToString().Should().Contain(
			"IServiceScopeFactory",
			"a provider left unscoped by accident must say so, not silently reproduce #70");
	}

	[TestMethod]
	[Description("The diagnostic for a scope-less provider must not repeat on every run: a long-lived host issuing many one-shot runs against the same provider would otherwise get one warning per run forever.")]
	public async Task When_TheSameScopeLessProviderRunsTwice_Then_TheDiagnosticFiresOnlyOnce()
	{
		var sut = ReplApp.Create(services => services.AddScoped<ScopedProbe>());
		sut.Map("scoped", (ScopedProbe probe) => probe.Id.ToString());
		var services = new ScopeLessServiceProvider(sut);
		using var diagnostics = new StringWriter();
		using var session = ReplSessionIO.SetSession(TextWriter.Null, TextReader.Null, error: diagnostics);

		await sut.RunAsync(["scoped", "--no-logo"], services).ConfigureAwait(false);
		await sut.RunAsync(["scoped", "--no-logo"], services).ConfigureAwait(false);

		var text = diagnostics.ToString();
		var occurrences = text.Split("IServiceScopeFactory", StringSplitOptions.None).Length - 1;
		occurrences.Should().Be(1, "the same provider must not be warned about twice");
	}
}
