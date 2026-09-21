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
	private bool _disposed;

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
			// Re-checked inside the gate: a caller that passed the check above can be queued here while
			// the session is disposed, and must not go on to run against a disposed scope.
			ThrowIfDisposed();

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
		finally
		{
			_commandGate.Release();
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
	/// Does not wait for a command that is still running: an orphaned run would make this hang. Such a
	/// run keeps the scope instead of having it disposed underneath it, so the scope outlives this
	/// handle in that case.
	/// </remarks>
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_owner.RemoveSession(SessionId);
		ReplSessionIO.RemoveSession(SessionId);

		// The gate is taken, never waited on. Waiting would turn an await using into a hang whenever a
		// command is still running — an orphaned timed-out run holds the gate until it finishes on its
		// own. Commands queued behind it stop at the re-check inside the gate instead.
		var held = await _commandGate.WaitAsync(TimeSpan.Zero).ConfigureAwait(false);
		try
		{
			if (!held)
			{
				// A command is mid-run and resolving from this very provider. Disposing it here would
				// pull the scope out from under that command, so its services start throwing
				// ObjectDisposedException and its scoped disposables die while it still holds them.
				// The orphan keeps the scope instead: it outlives the handle rather than faulting, and
				// the process reclaims it. Leaking a test session's scope is the lesser harm.
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
		finally
		{
			if (held)
			{
				_commandGate.Release();
			}
		}

		// The semaphore is deliberately not disposed: disposing it while a caller is queued on
		// WaitAsync strands that caller forever, and it owns no unmanaged resource here because
		// AvailableWaitHandle is never used.
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
		ObjectDisposedException.ThrowIf(_disposed, this);
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
