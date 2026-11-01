using System.Diagnostics.CodeAnalysis;
using Repl;

namespace HostingRemoteSample;

internal sealed class SignalRSessions(ReplApp app, IServiceProvider services, IHostApplicationLifetime lifetime, SessionTracker tracker)
{
	private readonly Lock _gate = new();
	private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
	private bool _stopping;

	internal void Start(string id, StreamedReplHost host, string name, ReplRunOptions options, CancellationToken disconnected)
	{
		lock (_gate)
		{
			if (_stopping || lifetime.ApplicationStopping.IsCancellationRequested)
			{
				throw new OperationCanceledException(lifetime.ApplicationStopping);
			}
			var cts = new SessionLifetime(lifetime.ApplicationStopping, disconnected);
			var session = new Session(host, cts, name);
			_sessions.Add(id, session);
			// Publish the run while holding the same lock used by shutdown and disconnect.
			session.RunTask = Task.Run(() => RunAsync(session, options), CancellationToken.None);
		}
	}

	internal bool TryGet(string id, [NotNullWhen(true)] out StreamedReplHost? host, [NotNullWhen(true)] out string? name)
	{
		lock (_gate)
		{
			if (_sessions.TryGetValue(id, out var session))
			{
				host = session.Host;
				name = session.Name;
				return true;
			}
		}
		host = null;
		name = null;
		return false;
	}

	internal Task DisconnectAsync(string id)
	{
		lock (_gate)
		{
			return _sessions.TryGetValue(id, out var session)
				? session.CloseTask ??= CloseAsync(id, session) : Task.CompletedTask;
		}
	}

	internal Task DrainAsync()
	{
		lock (_gate)
		{
			_stopping = true;
			return Task.WhenAll(_sessions.Keys.ToArray().Select(DisconnectAsync));
		}
	}

	private async Task RunAsync(Session session, ReplRunOptions options)
	{
		try { await session.Host.RunSessionAsync(app, services, options, session.Lifetime.Token).ConfigureAwait(false); }
		catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { }
		finally { await session.Host.DisposeAsync().ConfigureAwait(false); }
	}

	private async Task CloseAsync(string id, Session session)
	{
		try
		{
			session.Host.Complete();
			try { await session.Lifetime.CancelAsync().ConfigureAwait(false); }
#pragma warning disable VSTHRD003 // A session run is independent of its disconnecting caller.
			finally { await session.RunTask.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
		}
		finally
		{
			try { await session.Lifetime.DisposeAsync().ConfigureAwait(false); }
			finally
			{
				tracker.Remove(session.Name);
				lock (_gate) { _sessions.Remove(id); }
			}
		}
	}

	private sealed class Session(StreamedReplHost host, SessionLifetime lifetime, string name)
	{
		internal StreamedReplHost Host { get; } = host;
		internal SessionLifetime Lifetime { get; } = lifetime;
		internal string Name { get; } = name;
		internal Task RunTask { get; set; } = Task.CompletedTask;
		internal Task? CloseTask { get; set; }
	}
}
