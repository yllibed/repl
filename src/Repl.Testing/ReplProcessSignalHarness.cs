using System.Diagnostics.CodeAnalysis;

namespace Repl.Testing;

/// <summary>
/// Drives the process-signal lifecycle deterministically, in memory, against an application under
/// test: a first signal claims the runs in flight and cancels them cooperatively, a second one steps
/// aside for the operating system.
/// <para>
/// This is a sibling of <see cref="ReplTestHost"/> rather than part of it. Sessions are isolated from
/// each other; signal handling is process-global, so a harness owns it for its lifetime and only one
/// can exist at a time. <b>Dispose it</b> — ownership is released there and nowhere else, so a harness
/// that is never disposed leaves every later run with automatic signal handling refused for the rest
/// of the process. Constructing a second one while the first is alive throws instead of
/// producing a flaky pair — <b>configure your test framework not to run these in parallel</b>:
/// MSTest <c>[DoNotParallelize]</c>, xUnit a shared <c>[Collection]</c> or
/// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>, NUnit
/// <c>[NonParallelizable]</c>. Sharding across separate processes needs none of this.
/// </para>
/// <para>
/// What it proves: the framework's decision about each signal, the diagnostics it writes, the exit
/// code the run resolves to, and every platform wiring decision through
/// <see cref="ReplProcessSignalOptions.Platform"/>. What it cannot prove: that a process would
/// actually die. Nothing dies here, so a second signal reports
/// <see cref="ReplSignalDelivery.WouldTerminateProcess"/> and execution continues. That half needs a
/// spawned process on the matching platform.
/// </para>
/// <para>
/// <b>No operating-system signal registration is ever installed</b> — no <c>PosixSignalRegistration</c>,
/// on any platform. What is not isolated, and cannot be, is the console cancel-key handler: starting a
/// run registers one, because arbitrating Ctrl+C between an interactive session and a standalone run is
/// part of what these tests exist to exercise, and the subscription behind it is installed once per
/// process and never removed by design. So while a run is in flight, a <i>real</i> Ctrl+C aimed at your
/// test runner is claimed by the run under test and the first press does not stop it — press again to
/// escalate. See <see cref="ReplPlatformProfile"/> for how a declared platform affects that.
/// </para>
/// </summary>
public sealed class ReplProcessSignalHarness : IAsyncDisposable
{
	// Real elapsed time bounds a run that will not cooperate, so the provider is passed explicitly
	// rather than left implicit.
	private static readonly TimeProvider Clock = TimeProvider.System;

	private readonly Func<ReplApp> _appFactory;
	private readonly ReplProcessSignalOptions _options;
	private readonly IDisposable _isolation;
	private readonly IDisposable _ownership;
	private readonly SemaphoreSlim _startGate = new(initialCount: 1, maxCount: 1);
	private readonly List<Task<ReplSignalRunResult>> _runs = [];
	private readonly List<Task<ReplSignalRunResult>> _bounded = [];
	private readonly StringWriter _deliveryDiagnostics = new();
	private readonly ScopeRegistrationSignal _scopeRegistered;
	private bool _disposed;

	/// <summary>
	/// Everything the framework wrote about the signals delivered through this harness: the line naming
	/// a claimed signal, and the one saying a later signal is being left to the operating system.
	/// <para>
	/// These belong to the delivery, not to a run. A real signal callback runs on its own thread with
	/// no session, so the framework's diagnostics reach the process's error stream rather than any
	/// run's captured output — <see cref="ReplSignalRunResult.DiagnosticText"/> holds what the run
	/// itself wrote, which is a different thing.
	/// </para>
	/// </summary>
	public string DiagnosticText =>
		_options.NormalizeAnsi
			? ReplTestText.NormalizeOutput(_deliveryDiagnostics.ToString())
			: _deliveryDiagnostics.ToString();

	private ReplProcessSignalHarness(
		Func<ReplApp> appFactory,
		ReplProcessSignalOptions options,
		IDisposable isolation,
		IDisposable ownership,
		ScopeRegistrationSignal scopeRegistered)
	{
		_appFactory = appFactory;
		_options = options;
		_isolation = isolation;
		_ownership = ownership;
		_scopeRegistered = scopeRegistered;
	}

	/// <summary>
	/// Takes ownership of process-signal handling and returns a harness over an application factory.
	/// The factory is invoked once per run, so each run gets its own application instance.
	/// </summary>
	/// <param name="appFactory">Builds the application under test.</param>
	/// <param name="configure">Adjusts the harness options.</param>
	/// <returns>A harness holding process-signal ownership until it is disposed.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="appFactory"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException"><paramref name="configure"/> set <see cref="ReplProcessSignalOptions.Platform"/> to <see langword="null"/>.</exception>
	/// <exception cref="InvalidOperationException">Another harness is already active in this process, or a run with automatic signal handling is already in flight.</exception>
	public static ReplProcessSignalHarness Create(
		Func<ReplApp> appFactory,
		Action<ReplProcessSignalOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(appFactory);
		var options = new ReplProcessSignalOptions();
		configure?.Invoke(options);
		if (options.Platform is null)
		{
			throw new ArgumentException(
				$"{nameof(ReplProcessSignalOptions)}.{nameof(ReplProcessSignalOptions.Platform)} cannot be set to null.",
				nameof(configure));
		}

		// Taken atomically with the check that nothing else holds the epoch: a snapshot would let a run
		// register in the gap, and taking ownership tears down registrations without removing the scopes
		// using them — so that run would keep its place in the epoch and be cancelled by this harness's
		// first signal. While the claim is held the coordinator refuses any run this harness did not
		// launch, which is loud rather than quietly wrong.
		// Console cancel-key selection is exclusive: an interactive session takes Ctrl+C instead of the
		// standalone handlers, so a harness created alongside one would deliver Interrupt into that
		// session and be told it was handled while its own run went untouched — a green test asserting
		// something that never happened.
		if (ConsoleCancelKeyCoordinator.HasInteractiveHandlersForTesting)
		{
			throw new InvalidOperationException(
				"An interactive session currently owns the console cancel keys in this process. It takes "
				+ "Ctrl+C instead of a standalone run, so a signal delivered here would reach that session "
				+ "rather than the run under test: end the interactive session before creating the harness.");
		}

		var ownership = ProcessSignalCoordinator.TryClaimTestOwnership()
			?? throw new InvalidOperationException(
				"Process-signal handling in this process is already owned — either by another harness, or "
				+ "by a run with automatic signal handling that is still in flight. Only one owner at a "
				+ "time: let it finish, and configure your test framework not to run signal tests in "
				+ "parallel with anything that starts an automatic run.");

		try
		{
			// The readiness callback is handed to the isolation scope rather than parked on the
			// coordinator, so all of this harness's reach into process-global state has one owner and one
			// teardown — and cannot outlive the harness that installed it.
			var scopeRegistered = new ScopeRegistrationSignal();
			var isolation = ProcessSignalCoordinator.IsolateRegistrationsForTesting(
				registrationFault: options.RegistrationFault,
				policy: options.Platform.ToPolicy(),
				scopeRegisteredCallback: scopeRegistered.Signal);
			return new ReplProcessSignalHarness(appFactory, options, isolation, ownership, scopeRegistered);
		}
		catch
		{
			ownership.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Starts a run and returns once its signal scope has joined the ownership epoch, so a signal
	/// delivered afterwards reaches it instead of falling through as inert.
	/// <para>
	/// That guarantee is about the signal, not about progress. The scope is installed around the whole
	/// run, before its arguments are even parsed, so when this returns the command body has usually not
	/// started yet. A test that needs the command to be executing — to assert its cleanup ran, say —
	/// must have the command say so: complete a <see cref="TaskCompletionSource"/> from inside the
	/// handler and await it before delivering the signal.
	/// </para>
	/// <para>
	/// The run keeps executing after this returns. Call it again to put a second run in flight — that
	/// is what a late joiner is, and it is why starting is serialised while the runs are not. A late
	/// joiner needs the earlier run to still hold its scope: once the last run of a claimed epoch
	/// finishes, the epoch resets and the next signal is a first signal again.
	/// </para>
	/// </summary>
	/// <param name="commandLine">The command line to run. Split on whitespace, with double quotes grouping — not a shell parser: single quotes are literal and escape sequences are not interpreted.</param>
	/// <param name="cancellationToken">Cancels waiting for the run to register, and the run itself.</param>
	/// <returns>The run, still in flight.</returns>
	/// <exception cref="ArgumentException"><paramref name="commandLine"/> is empty or whitespace.</exception>
	/// <exception cref="ObjectDisposedException">The harness has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The run finished without ever registering a signal scope, which means the application never took process-signal ownership.</exception>
	public async ValueTask<ReplSignalRun> StartRunAsync(
		string commandLine,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
		ThrowIfDisposed();

		// Serialised so the readiness signal below cannot be consumed by a concurrent start. The runs
		// themselves stay concurrent; only their starts queue.
		await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// Re-checked: the first check happened before this wait, and disposal may have taken the gate
			// in between. Starting now would run outside an isolation that has already been torn down.
			ThrowIfDisposed();
			var runToken = new object();
			var registered = _scopeRegistered.Arm(runToken);
			// Marked before the run is launched so the marker flows into the context where the run's
			// ProcessSignalCancellationScope is constructed, letting the coordinator tell this harness's
			// scopes from anybody else's.
			using var owned = ProcessSignalCoordinator.MarkOwnedRunForTesting(runToken);
			var completion = Task.Run(
				() => ExecuteRunAsync(commandLine, cancellationToken),
				CancellationToken.None);
			// Both are retained. The raw task is what tells an abandoned run from a finished one during
			// the drain; the wrapper is what the caller is handed, so it is also what can fault unobserved
			// when a caller deliberately never awaits it — which one of this suite's own tests does.
			var bounded = BoundByWallClockAsync(completion, commandLine);
			_runs.Add(completion);
			_bounded.Add(bounded);

			try
			{
				await WaitForRegistrationAsync(
					registered.Task,
					completion,
					commandLine,
					cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				// This wait gave up on a registration that may still arrive. Drop the reservation so the
				// late registration cannot be paired with a later start's wait instead.
				_scopeRegistered.Abandon(runToken);
				throw;
			}

			return new ReplSignalRun(commandLine, bounded);
		}
		finally
		{
			_startGate.Release();
		}
	}

	/// <summary>
	/// Delivers a signal the way the operating system would, and reports what the framework decided.
	/// <para>
	/// Synchronous on purpose: a signal callback owes the operating system a suppression decision
	/// before it returns, so the framework decides synchronously and so does this. Await
	/// <see cref="ReplSignalRun.Completion"/> to see what the decision did to a run.
	/// </para>
	/// </summary>
	/// <param name="signal">The signal to deliver.</param>
	/// <returns>What the framework decided about this delivery.</returns>
	/// <exception cref="ObjectDisposedException">The harness has been disposed.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="signal"/> is not a known signal.</exception>
	public ReplSignalDelivery SendSignal(ReplProcessSignal signal)
	{
		ThrowIfDisposed();

		// The framework writes its signal diagnostics from whichever context delivers the signal. In
		// production that is an operating-system callback thread with no session of its own, so they
		// reach the real console and belong to no run in particular — which is why they are captured
		// here, on the harness, rather than folded into a run's result where they were never written.
		var sessionId = $"signal-delivery-{Guid.NewGuid():N}";
		using var session = ReplSessionIO.SetSession(
			TextWriter.Null,
			TextReader.Null,
			sessionId: sessionId,
			commandOutput: TextWriter.Null,
			error: _deliveryDiagnostics,
			isHostedSession: false);
		try
		{
			return Deliver(signal);
		}
		finally
		{
			ReplSessionIO.RemoveSession(sessionId);
		}
	}

	private ReplSignalDelivery Deliver(ReplProcessSignal signal)
	{
		// Re-checked rather than trusted from construction: an interactive session registered since then
		// would silently take this delivery, and reporting that as handled is worse than failing here.
		if (signal is not ReplProcessSignal.Terminate
			&& ConsoleCancelKeyCoordinator.HasInteractiveHandlersForTesting)
		{
			throw new InvalidOperationException(
				$"An interactive session owns the console cancel keys, so {signal} would reach it rather "
				+ "than the run under test. Only Terminate bypasses that selection, because SIGTERM does "
				+ "not participate in the interactive console-key priority rule.");
		}

		// Interrupt and Break go through console cancel-key arbitration, so an interactive owner wins
		// exactly as it does in production. Terminate has no such layer and reaches the claim directly:
		// SIGTERM does not participate in the interactive console-key priority rule. That asymmetry is
		// the framework's, so the routing is fixed rather than configurable — sending SIGTERM through
		// the console path would fake a priority rule that does not exist.
		var decision = signal switch
		{
			ReplProcessSignal.Interrupt => ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting(
				ConsoleSpecialKey.ControlC,
				isWindows: _options.Platform.IsWindows),
			ReplProcessSignal.Break => ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting(
				ConsoleSpecialKey.ControlBreak,
				isWindows: _options.Platform.IsWindows),
			// Gated on the platform in force actually wiring SIGTERM up. On a declared Windows profile the
			// framework installs no SIGTERM registration, and on an unsupported one it installs nothing at
			// all, so claiming here would have a cross-platform test assert a cancellation that could
			// never happen on the platform it names.
			ReplProcessSignal.Terminate when !ProcessSignalCoordinator.SigTermRegistrationDeclaredForTesting =>
				ConsoleCancelKeyHandlingResult.NotHandled,
			ReplProcessSignal.Terminate => ProcessSignalCoordinator.HandleSigTermForTesting(),
			_ => throw new ArgumentOutOfRangeException(
				nameof(signal),
				signal,
				"Unknown process signal."),
		};

		return decision switch
		{
			ConsoleCancelKeyHandlingResult.SuppressProcessTermination => ReplSignalDelivery.CancellationRequested,
			ConsoleCancelKeyHandlingResult.AllowProcessTermination => ReplSignalDelivery.WouldTerminateProcess,
			_ => ReplSignalDelivery.NotHandled,
		};
	}

	/// <summary>
	/// Releases process-signal ownership.
	/// <para>
	/// Waits for every run it started first. That is not politeness: a run still in flight still holds
	/// a scope in the ownership epoch, and letting it outlive the harness would leak that epoch into
	/// whatever runs next. <see cref="ReplProcessSignalOptions.RunTimeout"/> is what guarantees the
	/// wait ends, so a harness whose timeout is disabled and whose run was never released will block
	/// here.
	/// </para>
	/// </summary>
	/// <exception cref="InvalidOperationException">
	/// One or more runs were still executing and could not be stopped — a command that never observes
	/// its cancellation token cannot be interrupted. Ownership is released before this is raised, so the
	/// next harness can still be created; it is reported because such a run still holds a place in the
	/// process-wide signal epoch.
	/// </exception>
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		var abandoned = 0;
		var leakedScopes = 0;
		try
		{
			// Taken so draining cannot enumerate the run list while a start is appending to it, and so a
			// start already past its own gate check finishes registering before the callback goes away.
			await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
			try
			{
				abandoned = await DrainRunsAsync().ConfigureAwait(false);
				ObserveBoundedTasks();
				_runs.Clear();
				_bounded.Clear();
			}
			finally
			{
				_startGate.Release();
			}
		}
		finally
		{
			// Read before ownership goes: a scope this harness never started — a command that launched its
			// own automatic run, say — stays in the epoch, and every later harness is then refused with
			// nothing saying why. Reported below rather than prevented, since nothing here can drain it.
			leakedScopes = ProcessSignalCoordinator.ActiveScopeCountForTesting;

			// Unconditionally: anything thrown above would otherwise leave the exclusivity flag set and
			// the coordinator isolated for the rest of the process, turning one failed disposal into
			// every later harness in the suite refusing to start.
			_isolation.Dispose();
			_startGate.Dispose();
			_deliveryDiagnostics.Dispose();
			_ownership.Dispose();
		}

		// Reported after ownership is released, so the next harness can still be created — but reported,
		// because a run still holding a scope outlives the isolation that was just torn down, and every
		// later signal test in this process inherits that epoch.
		ReportWhatOutlivedTheHarness(abandoned, leakedScopes);
	}

	// Raised after ownership is released, so the next harness can still be created. Reported at all
	// because both states leave the process-wide epoch occupied by something this harness cannot drain,
	// and the alternative is a later test failing for a reason nothing explains.
	private static void ReportWhatOutlivedTheHarness(int abandoned, int leakedScopes)
	{
		if (abandoned > 0)
		{
			throw new InvalidOperationException(
				$"{abandoned} run(s) were still executing when the harness was disposed and could not be "
				+ "stopped: a command that never observes its cancellation token cannot be interrupted. "
				+ "They still hold a place in the process-wide signal epoch, so later signal tests in "
				+ "this process may see cancellations they did not cause.");
		}

		if (leakedScopes > 0)
		{
			throw new InvalidOperationException(
				$"{leakedScopes} process-signal scope(s) outlived the harness without having been started "
				+ "through it — a command under test started its own run with automatic signal handling. "
				+ "The harness cannot drain what it did not start, and while those scopes hold the epoch "
				+ "every later harness in this process is refused.");
		}
	}

	[SuppressMessage(
		"Design",
		"CA1031:Do not catch general exception types",
		Justification = "Disposal exists to drain the ownership epoch before the next test. A run that failed or timed out has already reported that through its own Completion, which the test either awaited or chose not to; rethrowing it from a using block would replace the test's own failure with this one.")]
	private async Task<int> DrainRunsAsync()
	{
		var abandoned = 0;
		foreach (var run in _runs)
		{
			try
			{
				_ = HasRunTimeout
					? await run.WaitAsync(DrainTimeout, Clock).ConfigureAwait(false)
					: await run.ConfigureAwait(false);
			}
			catch (TimeoutException) when (run.IsCompleted)
			{
				// The run itself ended on its own timeout. That is a finished run reporting a failure the
				// test has already seen, not one this drain gave up on.
			}
			catch (TimeoutException)
			{
				// Still running, and nothing here can stop it. Observe whatever it eventually produces so
				// it does not resurface as an unobserved task exception, and report it below.
				abandoned++;
				_ = run.ContinueWith(
					static observed => _ = observed.Exception,
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
			}
			catch (Exception)
			{
				// Intentionally observed and dropped; see the justification above.
			}
		}

		return abandoned;
	}

	// Cancelling a run only asks it to stop. A command that never observes its token blocks forever, so
	// the timeout has to be measured against the clock rather than against the run's cooperation —
	// otherwise the one guarantee that keeps a signal test from hanging a suite is the one it cannot
	// make. The run itself cannot be killed; it is abandoned, and disposal reports it.
	private async Task<ReplSignalRunResult> BoundByWallClockAsync(
		Task<ReplSignalRunResult> run,
		string commandLine)
	{
#pragma warning disable VSTHRD003 // Started by StartRunAsync, one frame up.
		if (!HasRunTimeout)
		{
			return await run.ConfigureAwait(false);
		}

		try
		{
			return await run.WaitAsync(_options.RunTimeout, Clock).ConfigureAwait(false);
		}
		catch (TimeoutException) when (!run.IsCompleted)
		{
			// Only when the wait is what gave up. A TimeoutException the run itself produced — a slow
			// provider build, say — propagates untouched: replacing it would send the test after a
			// deadline that never elapsed instead of the failure that actually happened.
			throw CreateTimeoutException(commandLine);
		}
#pragma warning restore VSTHRD003
	}

	private bool HasRunTimeout => ReplTestTimeout.IsEnabled(_options.RunTimeout);

	// Twice the run timeout, because draining can begin before a run's own timeout has elapsed and the
	// run still has to unwind once it fires. A cooperative run therefore always finishes within this;
	// only one that never observes its token is still here at the end, which is what it is measuring.
	private TimeSpan DrainTimeout => _options.RunTimeout + _options.RunTimeout;

	// Every task handed to a caller is observed, whether or not the caller awaited it. A test that
	// deliberately never reads Completion — because the run's outcome is not what it is asserting — must
	// not leave a faulted task for TaskScheduler.UnobservedTaskException to raise later.
	private void ObserveBoundedTasks()
	{
		foreach (var bounded in _bounded)
		{
			if (bounded.IsCompleted)
			{
				_ = bounded.Exception;
				continue;
			}

			_ = bounded.ContinueWith(
				static observed => _ = observed.Exception,
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}
	}

	private async Task WaitForRegistrationAsync(
		Task registered,
		Task<ReplSignalRunResult> completion,
		string commandLine,
		CancellationToken cancellationToken)
	{
#pragma warning disable VSTHRD003 // Both tasks were started by the caller one frame up, not handed in from elsewhere.
		// Bounded here as well as around the run. The run's own timeout starts inside ExecuteRunAsync,
		// after the application factory has returned — so a factory that blocks would otherwise hang this
		// wait with nothing to stop it, and the token documented as cancelling it would do nothing.
		var pending = Task.WhenAny(registered, completion);
		_ = HasRunTimeout
			? await pending.WaitAsync(_options.RunTimeout, Clock, cancellationToken).ConfigureAwait(false)
			: await pending.WaitAsync(cancellationToken).ConfigureAwait(false);

		// Which task WhenAny hands back is not the question — a run short enough to finish before this
		// resumes has both of them complete, and picking the loser would fail a perfectly good start.
		// The question is whether the scope ever joined the epoch.
		if (registered.IsCompleted)
		{
			return;
		}

		// It did not. Await the run first so a real failure is reported as itself; if it succeeded, the
		// application never took process-signal ownership, which would otherwise show up only as every
		// later delivery being silently inert.
		_ = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
		throw new InvalidOperationException(
			$"The run '{commandLine}' finished without registering a process-signal scope, so no signal "
			+ "could have reached it. The application under test did not take process-signal ownership: "
			+ "check that its profile or run options leave automatic handling enabled.");
	}

	private async Task<ReplSignalRunResult> ExecuteRunAsync(
		string commandLine,
		CancellationToken cancellationToken)
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		// The process-owning overload installs no session of its own, so without this the run writes
		// straight to the real console and nothing it produced could be asserted. Its own id, removed
		// afterwards, so a suite of signal tests does not accumulate session entries.
		var sessionId = $"signal-run-{Guid.NewGuid():N}";
		var observer = new RunObserver();
		var app = _appFactory();
		int exitCode;
		try
		{
			// isHostedSession decides the runtime channel: left at its default the run would take
			// ReplRuntimeChannel.Session, hiding commands gated to the CLI channel and telling handlers
			// they are in a hosted session. This harness models a process-owning standalone invocation,
			// so it has to say so.
			using (ReplSessionIO.SetSession(
				output,
				TextReader.Null,
				sessionId: sessionId,
				commandOutput: output,
				error: error,
				isHostedSession: false))
			{
				app.Core.ExecutionObserver = observer;
				using var timeout = ReplTestTimeout.CreateSource(_options.RunTimeout, cancellationToken);
				exitCode = await RunAsync(app, commandLine, timeout, cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			// The scope restores the ambient writers but does not remove an explicitly named session, so
			// a run that failed would otherwise leave its entry in the process-wide dictionary forever.
			ReplSessionIO.RemoveSession(sessionId);
		}

		var outputText = output.ToString();
		var diagnosticText = error.ToString();
		if (_options.NormalizeAnsi)
		{
			outputText = ReplTestText.NormalizeOutput(outputText);
			diagnosticText = ReplTestText.NormalizeOutput(diagnosticText);
		}

		return new ReplSignalRunResult
		{
			ExitCode = exitCode,
			OutcomeKind = observer.OutcomeKind,
			OutputText = outputText,
			DiagnosticText = diagnosticText,
		};
	}

	private static async Task<int> RunAsync(
		ReplApp app,
		string commandLine,
		CancellationTokenSource? timeout,
		CancellationToken cancellationToken)
	{
		int exitCode;
		try
		{
			// The overload taking no IServiceProvider, IHost or IReplHost is the only one that installs
			// the standalone signal bridge. Every other overload writes a diagnostic and drops
			// ProcessSignalHandling, so routing this through one of them would leave every delivery
			// inert with nothing but an unasserted line to show for it.
			exitCode = await app.RunAsync(
				ReplTestText.Tokenize(commandLine),
				new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.Automatic },
				timeout?.Token ?? cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (ReplTestTimeout.Expired(timeout, cancellationToken))
		{
			throw CreateTimeoutException(commandLine);
		}
		finally
		{
			app.Core.ExecutionObserver = null;
		}

		// An application that maps cancellation to an exit code, or a command that swallows it, returns
		// normally and never reaches the filter above. Reporting that as a result would hand the test a
		// passing run for a signal that never arrived.
		return ReplTestTimeout.Expired(timeout, cancellationToken)
			? throw CreateTimeoutException(commandLine)
			: exitCode;
	}

	private static TimeoutException CreateTimeoutException(string commandLine) =>
		new($"The run '{commandLine}' exceeded its timeout. A signal test starts a run that blocks "
			+ "until it is cancelled, so this usually means the signal never claimed it.");

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	/// <summary>
	/// Carries the "a scope has joined the epoch" notification from the coordinator to whichever start
	/// is waiting for it. Starts are serialised, so at most one is ever armed.
	/// </summary>
	/// <summary>
	/// Pairs each "a scope joined the epoch" notification with the start that caused it.
	/// <para>
	/// Keyed by run token rather than ordered, because order is not something this can rely on: a start
	/// whose wait gave up leaves its launch running, and that launch may register long afterwards. With
	/// a queue its late registration would be handed to whichever start was waiting by then, reporting
	/// that run as having joined the epoch when it had not — and a signal sent next would cancel the
	/// abandoned run while missing the one just returned to the caller.
	/// </para>
	/// </summary>
	private sealed class ScopeRegistrationSignal
	{
		private readonly Lock _gate = new();
		private readonly Dictionary<object, TaskCompletionSource> _pending = [];

		public TaskCompletionSource Arm(object runToken)
		{
			var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			lock (_gate)
			{
				_pending[runToken] = pending;
			}

			return pending;
		}

		public void Signal(object? runToken)
		{
			if (runToken is null)
			{
				return;
			}

			TaskCompletionSource? pending;
			lock (_gate)
			{
				_ = _pending.Remove(runToken, out pending);
			}

			pending?.TrySetResult();
		}

		/// <summary>
		/// Drops a wait whose start gave up, so the entry does not linger. Its launch may still register
		/// later; with nothing left under that token, the notification is simply discarded instead of
		/// being handed to another start.
		/// </summary>
		public void Abandon(object runToken)
		{
			lock (_gate)
			{
				_ = _pending.Remove(runToken);
			}
		}
	}

	private sealed class RunObserver : IReplExecutionObserver
	{
		// Every run that returns an exit code reports its kind while that code is resolved, so this is
		// only ever read after the run completed.
		public ReplExecutionOutcomeKind OutcomeKind { get; private set; } = ReplExecutionOutcomeKind.Success;

		public void OnResult(object? result)
		{
		}

		public void OnOutcome(ReplExecutionOutcomeKind kind) => OutcomeKind = kind;

		public void OnInteractionEvent(ReplInteractionEvent evt)
		{
		}
	}
}
