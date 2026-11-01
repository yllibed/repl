namespace Repl.Remote;

/// <summary>Application adapter used by the protocol, independent of any engine or DI container.</summary>
public interface IRemoteReplBackend : IAsyncDisposable
{
	/// <summary>Describes the exposed command graph.</summary>
	ValueTask<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken);
	/// <summary>Runs a command using the application's ordinary invocation pipeline.</summary>
	ValueTask<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken);
	/// <summary>Starts a terminal whose output is delivered through the supplied asynchronous sink.</summary>
	ValueTask<IRemoteTerminalSession> OpenTerminalAsync(RemoteTerminalOptions options,
		Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken);
}
