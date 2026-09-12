using System.Diagnostics.CodeAnalysis;

namespace Repl.Testing;

/// <summary>
/// Drives the process-signal lifecycle deterministically, in memory, against an application under
/// test: a first signal claims the runs in flight and cancels them cooperatively, a second one steps
/// aside for the operating system.
/// <para>
/// This is a sibling of <see cref="ReplTestHost"/> rather than part of it. Sessions are isolated from
/// each other; signal handling is process-global, so a harness owns it for its lifetime and only one
/// can exist at a time. Constructing a second one while the first is alive throws instead of
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
/// <b>No operating-system signal registration is ever installed.</b> One thing is not isolated though,
/// and cannot be: starting a run registers a console cancel-key handler, which is how the framework
/// arbitrates Ctrl+C between an interactive session and a standalone run, and that arbitration is part
/// of what these tests exist to exercise. The subscription behind it is installed once per process and
/// never removed, by design. So while a run is in flight, a <i>real</i> Ctrl+C aimed at your test
/// runner is claimed cooperatively by the run under test and the first press does not stop it — press
/// again to escalate.
/// </para>
/// </summary>
public sealed class ReplProcessSignalHarness : IAsyncDisposable
{
	private static int s_active;

	private readonly Func<ReplApp> _appFactory;
	private readonly ReplProcessSignalOptions _options;
	private readonly IDisposable _isolation;
	private readonly SemaphoreSlim _startGate = new(initialCount: 1, maxCount: 1);
	private readonly List<Task<ReplSignalRunResult>> _runs = [];
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
		ScopeRegistrationSignal scopeRegistered)
	{
		_appFactory = appFactory;
		_options = options;
		_isolation = isolation;
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

		// Taking ownership tears down the registrations without touching the scopes that were using
		// them, so a run already in flight would keep its place in the epoch and be cancelled by the
		// first signal this harness delivers. Refuse rather than corrupt something unrelated.
		if (ProcessSignalCoordinator.ActiveScopeCountForTesting != 0)
		{
			throw new InvalidOperationException(
				"A run with automatic process-signal handling is already in flight in this process. "
				+ "Taking signal ownership now would interfere with it: let it finish before creating "
				+ "the harness, and configure your test framework not to run signal tests in parallel.");
		}

		// Two live harnesses would not merely race on the application under test: taking ownership
		// tears down and reinstalls the shared registration state, so the second would corrupt the
		// first's isolation. Failing loudly beats being intermittently wrong.
		if (Interlocked.CompareExchange(ref s_active, 1, 0) != 0)
		{
			throw new InvalidOperationException(
				"A process-signal test harness is already active in this process. Signal handling is "
				+ "process-global, so only one harness can own it at a time: dispose the previous "
				+ "harness before creating another, and configure your test framework not to run "
				+ "harness tests in parallel.");
		}

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
			return new ReplProcessSignalHarness(appFactory, options, isolation, scopeRegistered);
		}
		catch
		{
			Interlocked.Exchange(ref s_active, 0);
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
			var registered = _scopeRegistered.Arm();
			var completion = Task.Run(
				() => ExecuteRunAsync(commandLine, cancellationToken),
				CancellationToken.None);
			_runs.Add(completion);

			await WaitForRegistrationAsync(registered.Task, completion, commandLine).ConfigureAwait(false);
			return new ReplSignalRun(commandLine, completion);
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
			error: _deliveryDiagnostics);
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
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		try
		{
			// Taken so draining cannot enumerate the run list while a start is appending to it, and so a
			// start already past its own gate check finishes registering before the callback goes away.
			await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
			try
			{
				await DrainRunsAsync().ConfigureAwait(false);
				_runs.Clear();
			}
			finally
			{
				_startGate.Release();
			}
		}
		finally
		{
			// Unconditionally: anything thrown above would otherwise leave the exclusivity flag set and
			// the coordinator isolated for the rest of the process, turning one failed disposal into
			// every later harness in the suite refusing to start.
			_isolation.Dispose();
			_startGate.Dispose();
			_deliveryDiagnostics.Dispose();
			Interlocked.Exchange(ref s_active, 0);
		}
	}

	[SuppressMessage(
		"Design",
		"CA1031:Do not catch general exception types",
		Justification = "Disposal exists to drain the ownership epoch before the next test. A run that failed or timed out has already reported that through its own Completion, which the test either awaited or chose not to; rethrowing it from a using block would replace the test's own failure with this one.")]
	private async Task DrainRunsAsync()
	{
		foreach (var run in _runs)
		{
			try
			{
				_ = await run.ConfigureAwait(false);
			}
			catch (Exception)
			{
				// Intentionally observed and dropped; see the justification above.
			}
		}
	}

	private static async Task WaitForRegistrationAsync(
		Task registered,
		Task<ReplSignalRunResult> completion,
		string commandLine)
	{
#pragma warning disable VSTHRD003 // Both tasks were started by the caller one frame up, not handed in from elsewhere.
		_ = await Task.WhenAny(registered, completion).ConfigureAwait(false);

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
		_ = await completion.ConfigureAwait(false);
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
			using (ReplSessionIO.SetSession(
				output,
				TextReader.Null,
				sessionId: sessionId,
				commandOutput: output,
				error: error))
			{
				app.Core.ExecutionObserver = observer;
				using var timeout = CreateTimeoutSource(cancellationToken);
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
		catch (OperationCanceledException) when (IsRunTimeout(timeout, cancellationToken))
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
		return IsRunTimeout(timeout, cancellationToken)
			? throw CreateTimeoutException(commandLine)
			: exitCode;
	}

	private static TimeoutException CreateTimeoutException(string commandLine) =>
		new($"The run '{commandLine}' exceeded its timeout. A signal test starts a run that blocks "
			+ "until it is cancelled, so this usually means the signal never claimed it.");

	// The harness's own timeout fired, and not the caller's token.
	private static bool IsRunTimeout(CancellationTokenSource? timeout, CancellationToken cancellationToken) =>
		timeout is not null
		&& timeout.IsCancellationRequested
		&& !cancellationToken.IsCancellationRequested;

	private CancellationTokenSource? CreateTimeoutSource(CancellationToken cancellationToken)
	{
		if (_options.RunTimeout <= TimeSpan.Zero || _options.RunTimeout == Timeout.InfiniteTimeSpan)
		{
			return null;
		}

		var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		source.CancelAfter(_options.RunTimeout);
		return source;
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	/// <summary>
	/// Carries the "a scope has joined the epoch" notification from the coordinator to whichever start
	/// is waiting for it. Starts are serialised, so at most one is ever armed.
	/// </summary>
	private sealed class ScopeRegistrationSignal
	{
		private TaskCompletionSource? _pending;

		public TaskCompletionSource Arm()
		{
			var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Volatile.Write(ref _pending, pending);
			return pending;
		}

		public void Signal() => Volatile.Read(ref _pending)?.TrySetResult();
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
