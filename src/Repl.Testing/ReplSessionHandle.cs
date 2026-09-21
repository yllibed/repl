using Microsoft.Extensions.DependencyInjection;

namespace Repl.Testing;

/// <summary>
/// Handle for a single live in-memory REPL session.
/// </summary>
public sealed class ReplSessionHandle : IAsyncDisposable
{
	private readonly ReplTestHost _owner;
	private readonly ReplApp _app;
	private readonly ReplScenarioOptions _options;
	private readonly IServiceProvider _services;
	private readonly ReplRunOptions _runOptions;
	private readonly IReadOnlyDictionary<string, string>? _sessionAnswers;
	private readonly SemaphoreSlim _commandGate = new(initialCount: 1, maxCount: 1);
	private readonly string _sessionId;
	private int _disposed;

	// Coordinates disposal with a command that is still running, without a check-then-act race: bit 0
	// latches "disposal was requested", the rest counts commands currently executing against _services
	// (0 or 1 — _commandGate allows only one at a time, but a counter rather than a single bit keeps the
	// arithmetic below correct however many false starts land here). Both DisposeAsync and a running
	// command's own finally mutate this with one Interlocked op apiece; whichever one's read-modify-write
	// observes the other side's contribution already applied is the one that defers, and the one that
	// observes it absent is the one that disposes — so exactly one side ever does, never zero, never two
	// without DisposeServicesOnceAsync's own guard catching the duplicate.
	private int _serviceLifecycle;
	private int _servicesDisposeClaimed;
	private const int DisposalRequested = 1;
	private const int CommandInFlight = 2;

	private ReplSessionHandle(
		ReplTestHost owner,
		ReplApp app,
		ReplScenarioOptions options,
		IServiceProvider services,
		ReplRunOptions runOptions,
		IReadOnlyDictionary<string, string>? sessionAnswers,
		string sessionId)
	{
		_owner = owner;
		_app = app;
		_options = options;
		_services = services;
		_runOptions = runOptions;
		_sessionAnswers = sessionAnswers;
		_sessionId = sessionId;
	}

	/// <summary>
	/// This session's id, unique within its <see cref="ReplTestHost"/> and stable for the session's
	/// lifetime.
	/// </summary>
	public string SessionId => _sessionId;

	/// <summary>
	/// Runs a command in this session.
	/// </summary>
	/// <param name="commandText">The command text to execute.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The execution result.</returns>
	/// <exception cref="TimeoutException">
	/// The command exceeded <see cref="ReplScenarioOptions.CommandTimeout"/>, whether the app let the
	/// cancellation propagate or mapped it to an exit code through <c>ReplOptions.ExitCodes.Cancelled</c>.
	/// </exception>
	public ValueTask<CommandExecution> RunCommandAsync(
		string commandText,
		CancellationToken cancellationToken = default) =>
		ExecuteCommandCoreAsync(commandText, answers: null, cancellationToken);

	/// <summary>
	/// Runs a command in this session with prefilled answers for interactive prompts.
	/// </summary>
	/// <param name="commandText">The command text to execute.</param>
	/// <param name="answers">Prompt answers keyed by prompt name. Overrides session-level answers for the same name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The execution result.</returns>
	/// <exception cref="TimeoutException">
	/// The command exceeded <see cref="ReplScenarioOptions.CommandTimeout"/>, whether the app let the
	/// cancellation propagate or mapped it to an exit code through <c>ReplOptions.ExitCodes.Cancelled</c>.
	/// </exception>
	public ValueTask<CommandExecution> RunCommandAsync(
		string commandText,
		IReadOnlyDictionary<string, string> answers,
		CancellationToken cancellationToken = default) =>
		ExecuteCommandCoreAsync(commandText, answers, cancellationToken);

	private async ValueTask<CommandExecution> ExecuteCommandCoreAsync(
		string commandText,
		IReadOnlyDictionary<string, string>? answers,
		CancellationToken cancellationToken)
	{
		commandText = string.IsNullOrWhiteSpace(commandText)
			? throw new ArgumentException("Command text cannot be empty.", nameof(commandText))
			: commandText;
		ThrowIfDisposed();

		await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// Registered as in-flight BEFORE the re-check below, not after: this is what DisposeAsync's
			// own Interlocked.Or observes to decide whether it or this command owns disposing _services,
			// and that decision must already account for this command — otherwise a DisposeAsync racing
			// in between the check and the registration could dispose _services while this command still
			// believes it is clear to run.
			Interlocked.Add(ref _serviceLifecycle, CommandInFlight);

			// Re-checked inside the gate: a caller that passed the check above can be queued here while
			// the session is disposed, and must not go on to run against a disposed scope. Guaranteed
			// visible by now if a concurrent DisposeAsync's Or already observed the increment above,
			// because Interlocked operations on _serviceLifecycle are full fences on both sides.
			ThrowIfDisposed();

			return await RunWithinGateAsync(commandText, answers, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			var after = Interlocked.Add(ref _serviceLifecycle, -CommandInFlight);
			if ((after & DisposalRequested) != 0)
			{
				// DisposeAsync ran while this command held the gate and deferred to it rather than
				// disposing _services out from under a run in progress. Finish that disposal now, still
				// inside the gate, so a caller queued behind this one hits the re-check above instead of
				// a provider that is only half torn down.
				await DisposeServicesOnceAsync().ConfigureAwait(false);
			}

			_commandGate.Release();
		}
	}

	/// <summary>Runs one command's pipeline once the gate and the in-flight registration are held.</summary>
	private async ValueTask<CommandExecution> RunWithinGateAsync(
		string commandText,
		IReadOnlyDictionary<string, string>? answers,
		CancellationToken cancellationToken)
	{
		var startedAt = DateTimeOffset.UtcNow;
		using var output = new StringWriter();
		var host = new TestSessionHost(_sessionId, output);
		var observer = new SessionExecutionObserver();
		var args = BuildArgsWithAnswers(ReplTestText.Tokenize(commandText), _sessionAnswers, answers);
		using var timeout = ReplTestTimeout.CreateSource(_options.CommandTimeout, cancellationToken);
		var token = timeout?.Token ?? cancellationToken;

		_app.Core.ExecutionObserver = observer;
		int exitCode;
		try
		{
			exitCode = await _app.RunAsync(args, host, _services, _runOptions, token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (ReplTestTimeout.Expired(timeout, cancellationToken))
		{
			throw CreateTimeoutException(commandText);
		}
		finally
		{
			_app.Core.ExecutionObserver = null;
		}

		ThrowIfCancelledByTimeout(observer, timeout, commandText, cancellationToken);

		var outputText = output.ToString();
		if (_options.NormalizeAnsi)
		{
			outputText = ReplTestText.NormalizeOutput(outputText);
		}

		var timeline = BuildTimeline(outputText, observer.Events, observer.LastResult);
		return new CommandExecution(
			commandText,
			exitCode,
			outputText,
			observer.LastResult,
			observer.Events.ToArray(),
			timeline,
			startedAt,
			DateTimeOffset.UtcNow);
	}

	/// <summary>Disposes <see cref="_services"/> exactly once, however many callers request it.</summary>
	private async ValueTask DisposeServicesOnceAsync()
	{
		if (Interlocked.Exchange(ref _servicesDisposeClaimed, 1) != 0)
		{
			return;
		}

		switch (_services)
		{
			case IAsyncDisposable asyncDisposable:
				// Releases the session DI scope, disposing its Scoped services.
				await asyncDisposable.DisposeAsync().ConfigureAwait(false);
				break;
			case IDisposable disposable:
				disposable.Dispose();
				break;
		}
	}

	/// <summary>
	/// Captures the session's current terminal metadata. Returns
	/// <see cref="SessionSnapshot.Empty(string)"/> when the session has registered none yet, so this
	/// never returns <see langword="null"/>.
	/// </summary>
	/// <returns>A snapshot of this session.</returns>
	public SessionSnapshot GetSnapshot()
	{
		if (ReplSessionIO.TryGetSession(SessionId, out var session))
		{
			return new SessionSnapshot(
				session.SessionId,
				session.TransportName,
				session.RemotePeer,
				session.TerminalIdentity,
				session.WindowSize,
				session.TerminalCapabilities,
				session.AnsiSupport,
				session.LastUpdatedUtc);
		}

		return SessionSnapshot.Empty(SessionId);
	}

	/// <summary>
	/// Ends the session, removes it from its host, and releases the session's dependency-injection
	/// scope — disposing every <c>Scoped</c> service it resolved. Disposing twice is a no-op.
	/// </summary>
	/// <remarks>
	/// Never waits on a command that is still running — that would turn an <c>await using</c> into a
	/// hang for the lifetime of an orphaned run. It also never simply abandons that command's scope:
	/// disposal is handed to whichever side, this method or the running command's own <c>finally</c>,
	/// is the one still active when the other checks. See <see cref="_serviceLifecycle"/>.
	/// </remarks>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		_owner.RemoveSession(SessionId);
		ReplSessionIO.RemoveSession(SessionId);

		var before = Interlocked.Or(ref _serviceLifecycle, DisposalRequested);
		if ((before & CommandInFlight) == 0)
		{
			// No command was registered as in-flight at the moment this bit was set: none can appear
			// afterward, since ThrowIfDisposed now observes _disposed and refuses every later one before
			// it reaches the increment that would make it visible here. This call owns the disposal.
			await DisposeServicesOnceAsync().ConfigureAwait(false);
		}
		// Otherwise a command holds the gate right now. Its own finally, decrementing _serviceLifecycle
		// on the way out, is guaranteed to observe DisposalRequested — set here, and never cleared — and
		// disposes on this call's behalf before releasing the gate.
	}

	internal static ValueTask<ReplSessionHandle> StartAsync(
		ReplTestHost owner,
		Func<ReplApp> appFactory,
		SessionDescriptor descriptor,
		ReplScenarioOptions options,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(appFactory);
		ArgumentNullException.ThrowIfNull(descriptor);
		ArgumentNullException.ThrowIfNull(options);
		cancellationToken.ThrowIfCancellationRequested();

		var sessionId = $"session-{Guid.NewGuid():N}";
		ReplSessionIO.EnsureSession(sessionId);
		var app = appFactory();
		// The handle owns the session DI scope (each command is a separate one-shot run),
		// so every run opts out of per-run scoping.
		var runOptions = descriptor.BuildRunOptions(options) with { SessionScope = SessionScopeBehavior.CallerOwned };
		var services = SessionScopedTestServices.Create(app);
		var handle = new ReplSessionHandle(owner, app, options, services, runOptions, descriptor.Answers, sessionId);
		return ValueTask.FromResult(handle);
	}

	private static string[] BuildArgsWithAnswers(
		string[] baseTokens,
		IReadOnlyDictionary<string, string>? sessionAnswers,
		IReadOnlyDictionary<string, string>? commandAnswers)
	{
		if (sessionAnswers is null && commandAnswers is null)
		{
			return baseTokens;
		}

		var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (sessionAnswers is not null)
		{
			foreach (var pair in sessionAnswers)
			{
				merged[pair.Key] = pair.Value;
			}
		}

		if (commandAnswers is not null)
		{
			foreach (var pair in commandAnswers)
			{
				merged[pair.Key] = pair.Value;
			}
		}

		var args = new List<string>(baseTokens.Length + merged.Count);
		args.AddRange(baseTokens);
		foreach (var pair in merged)
		{
			args.Add($"--answer:{pair.Key}={pair.Value}");
		}

		return args.ToArray();
	}

	private static List<CommandEvent> BuildTimeline(
		string outputText,
		IReadOnlyList<ReplInteractionEvent> events,
		object? result)
	{
		var timeline = new List<CommandEvent>(capacity: events.Count + 2);
		if (!string.IsNullOrEmpty(outputText))
		{
			timeline.Add(new OutputWrittenEvent(outputText));
		}

		timeline.AddRange(events.Select(static evt => new InteractionObservedEvent(evt)));
		timeline.Add(new ResultProducedEvent(result));
		return timeline;
	}

	// An app that maps ReplOptions.ExitCodes.Cancelled returns a code instead of throwing, so the
	// exception filter around the run never fires. Gated on the run having actually reported a
	// cancellation, because the timeout token can also elapse while an already-completed run tears down.
	private void ThrowIfCancelledByTimeout(
		SessionExecutionObserver observer,
		CancellationTokenSource? timeout,
		string commandText,
		CancellationToken cancellationToken)
	{
		if (observer.WasCancelled && ReplTestTimeout.Expired(timeout, cancellationToken))
		{
			throw CreateTimeoutException(commandText);
		}
	}

	private TimeoutException CreateTimeoutException(string commandText) =>
		new($"Command '{commandText}' exceeded timeout of {_options.CommandTimeout.TotalMilliseconds:0} ms.");

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
	}

	private sealed class TestSessionHost(string sessionId, TextWriter output) : IReplSessionHost
	{
		public string SessionId { get; } = sessionId;

		public TextReader Input { get; } = TextReader.Null;

		public TextWriter Output { get; } = output;
	}

	private sealed class SessionExecutionObserver : IReplExecutionObserver
	{
		private readonly List<ReplInteractionEvent> _events = [];

		public object? LastResult { get; private set; }

		public IReadOnlyList<ReplInteractionEvent> Events => _events;

		/// <summary>
		/// Whether the run ended in a cancellation. Lets the handle tell a command the timeout actually
		/// interrupted from one that finished while the timeout token happened to elapse.
		/// </summary>
		public bool WasCancelled { get; private set; }

		public void OnResult(object? result) => LastResult = result;

		public void OnOutcome(ReplExecutionOutcomeKind kind) =>
			WasCancelled = kind is ReplExecutionOutcomeKind.Cancelled or ReplExecutionOutcomeKind.Interrupted;

		public void OnInteractionEvent(ReplInteractionEvent evt)
		{
			if (evt is not null)
			{
				_events.Add(evt);
			}
		}
	}

	// One logical test session spans several one-shot Run* calls, so the handle owns the session
	// DI scope itself: the provider is built over a scope of the APP services (user registrations
	// become visible to session commands, and Scoped instances are shared across the session
	// commands) and every run uses SessionScopeBehavior.CallerOwned so the Run* entry points do not
	// open a fresh scope — and fresh Scoped instances — per command.
	private sealed class SessionScopedTestServices : IServiceProvider, IAsyncDisposable
	{
		private readonly AsyncServiceScope _scope;

		private SessionScopedTestServices(AsyncServiceScope scope) => _scope = scope;

		// ReplApp.Services is always a built ServiceProvider, which always supplies the factory, so
		// there is no scope-less case to fall back to here — unlike the Run* paths, which take whatever
		// provider a caller hands them.
		public static SessionScopedTestServices Create(ReplApp app) =>
			new(app.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope());

		// IReplSessionState is resolved from the scope like everything else, rather than masked with a
		// harness-owned bag: the registration is Scoped, so the scope already gives this session its own
		// — and masking it would run a consumer's own implementation in production while this harness
		// silently exercised a different one.
		public object? GetService(Type serviceType) => _scope.ServiceProvider.GetService(serviceType);

		public ValueTask DisposeAsync() => _scope.DisposeAsync();
	}
}
