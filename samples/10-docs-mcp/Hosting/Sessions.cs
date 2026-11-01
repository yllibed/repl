using System.Collections.Concurrent;
using Repl.Remote;

namespace DocsMcpSample.Hosting;

internal sealed record SessionInfo(string Id, string Transport, DateTimeOffset ConnectedAt);

internal sealed class Sessions(TimeProvider time)
{
	private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
	internal SessionInfo[] List() => _sessions.Values.OrderBy(session => session.ConnectedAt).ToArray();
	internal string Add(string transport)
	{
		var id = Guid.NewGuid().ToString("N")[..8];
		_sessions[id] = new(id, transport, time.GetUtcNow());
		return id;
	}
	internal void Remove(string id) => _sessions.TryRemove(id, out _);

	internal IRemoteReplBackend Track(IRemoteReplBackend backend) => new TrackedBackend(backend, this);

	private sealed class TrackedBackend(IRemoteReplBackend backend, Sessions sessions) : IRemoteReplBackend
	{
		private readonly string _id = sessions.Add("local-cli");
		public ValueTask<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken) => backend.DescribeAsync(cancellationToken);
		public ValueTask<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken) => backend.ExecuteAsync(request, cancellationToken);
		public ValueTask<IRemoteTerminalSession> OpenTerminalAsync(RemoteTerminalOptions options, Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken) => backend.OpenTerminalAsync(options, writeOutput, cancellationToken);
		public async ValueTask DisposeAsync()
		{
			try { await backend.DisposeAsync(); }
			finally { sessions.Remove(_id); }
		}
	}
}
