using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace Repl.Testing;

/// <summary>
/// Handle for a single live in-memory REPL session.
/// </summary>
public sealed partial class ReplSessionHandle : IAsyncDisposable
{
	private readonly ReplTestHost _owner;
	private readonly ReplApp _app;
	private readonly ReplScenarioOptions _options;
	private readonly IServiceProvider _services;
	private readonly ReplRunOptions _runOptions;
	private readonly SemaphoreSlim _commandGate = new(initialCount: 1, maxCount: 1);
	private readonly string _sessionId;
	private bool _disposed;

	private ReplSessionHandle(
		ReplTestHost owner,
		ReplApp app,
		ReplScenarioOptions options,
		IServiceProvider services,
		ReplRunOptions runOptions,
		string sessionId)
	{
		_owner = owner;
		_app = app;
		_options = options;
		_services = services;
		_runOptions = runOptions;
		_sessionId = sessionId;
	}

	public string SessionId => _sessionId;

	public async ValueTask<CommandExecution> RunCommandAsync(
		string commandText,
		CancellationToken cancellationToken = default)
	{
		commandText = string.IsNullOrWhiteSpace(commandText)
			? throw new ArgumentException("Command text cannot be empty.", nameof(commandText))
			: commandText;
		ThrowIfDisposed();

		await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var startedAt = DateTimeOffset.UtcNow;
			using var output = new StringWriter();
			var host = new TestSessionHost(_sessionId, output);
			var observer = new SessionExecutionObserver();
			var args = Tokenize(commandText).ToArray();
			using var timeout = CreateTimeoutSource(cancellationToken);
			var token = timeout?.Token ?? cancellationToken;

			_app.Core.ExecutionObserver = observer;
			int exitCode;
			try
			{
				exitCode = await _app.RunAsync(args, host, _services, _runOptions, token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (timeout is not null && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				throw new TimeoutException(
					$"Command '{commandText}' exceeded timeout of {_options.CommandTimeout.TotalMilliseconds:0} ms.");
			}
			finally
			{
				_app.Core.ExecutionObserver = null;
			}

			var outputText = output.ToString();
			if (_options.NormalizeAnsi)
			{
				outputText = NormalizeOutput(outputText);
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

	public ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return ValueTask.CompletedTask;
		}

		_disposed = true;
		_owner.RemoveSession(SessionId);
		ReplSessionIO.RemoveSession(SessionId);
		if (_services is IDisposable disposable)
		{
			disposable.Dispose();
		}

		_commandGate.Dispose();
		return ValueTask.CompletedTask;
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
		var runOptions = descriptor.BuildRunOptions(options);
		var serviceCollection = new ServiceCollection();
		serviceCollection.AddSingleton<IReplSessionState, InMemorySessionState>();
		var services = serviceCollection.BuildServiceProvider();
		var handle = new ReplSessionHandle(owner, app, options, services, runOptions, sessionId);
		return ValueTask.FromResult(handle);
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

	private CancellationTokenSource? CreateTimeoutSource(CancellationToken cancellationToken)
	{
		if (_options.CommandTimeout <= TimeSpan.Zero || _options.CommandTimeout == Timeout.InfiniteTimeSpan)
		{
			return null;
		}

		var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_options.CommandTimeout);
		return timeout;
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}

	private static List<string> Tokenize(string value)
	{
		var tokens = new List<string>();
		var current = new System.Text.StringBuilder();
		var inQuotes = false;
		foreach (var ch in value)
		{
			if (ch == '"')
			{
				inQuotes = !inQuotes;
				continue;
			}

			if (!inQuotes && char.IsWhiteSpace(ch))
			{
				if (current.Length > 0)
				{
					tokens.Add(current.ToString());
					current.Clear();
				}

				continue;
			}

			current.Append(ch);
		}

		if (current.Length > 0)
		{
			tokens.Add(current.ToString());
		}

		return tokens;
	}

	private static string NormalizeOutput(string output)
	{
		if (string.IsNullOrEmpty(output))
		{
			return output;
		}

		var normalized = output.Replace("\r", string.Empty, StringComparison.Ordinal);
		return BuildAnsiEscapeRegex().Replace(normalized, string.Empty);
	}

	[GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.None, matchTimeoutMilliseconds: 50)]
	private static partial Regex BuildAnsiEscapeRegex();

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

		public void OnResult(object? result) => LastResult = result;

		public void OnInteractionEvent(ReplInteractionEvent evt)
		{
			if (evt is not null)
			{
				_events.Add(evt);
			}
		}
	}

	private sealed class InMemorySessionState : IReplSessionState
	{
		private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

		public bool TryGet<T>(string key, out T? value)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(key);
			if (_values.TryGetValue(key, out var existing) && existing is T typed)
			{
				value = typed;
				return true;
			}

			value = default;
			return false;
		}

		public T? Get<T>(string key)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(key);
			return TryGet<T>(key, out var value) ? value : default;
		}

		public void Set<T>(string key, T value)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(key);
			_values[key] = value;
		}

		public bool Remove(string key)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(key);
			return _values.Remove(key);
		}

		public void Clear() => _values.Clear();
	}
}
