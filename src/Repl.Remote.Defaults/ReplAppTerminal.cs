using Microsoft.Extensions.DependencyInjection;

namespace Repl.Remote;

internal sealed class ReplAppTerminal : IRemoteTerminalSession
{
	private readonly CancellationTokenSource _lifetime;
	private readonly StreamedReplHost _host;
	private readonly AsyncServiceScope _scope;
	private readonly TerminalWriter _writer;

	internal ReplAppTerminal(ReplApp app, IServiceProvider services, RemoteTerminalOptions options,
		Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken)
	{
		_lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_scope = services.CreateAsyncScope();
		_writer = new TerminalWriter(writeOutput, _lifetime);
		_host = new StreamedReplHost(_writer, new InitialSize(options.Columns, options.Rows))
		{
			TransportName = "remote-cli",
		};
		Completion = RunAsync(app, options);
	}

	private async Task<int> RunAsync(ReplApp app, RemoteTerminalOptions options)
	{
		try
		{
			return await _host.RunSessionAsync(app, _scope.ServiceProvider, new ReplRunOptions
			{
				SessionScope = SessionScopeBehavior.CallerOwned,
				TerminalOverrides = new()
				{
					TerminalIdentity = options.Terminal,
					AnsiSupported = options.Ansi,
					WindowSize = (options.Columns, options.Rows),
				},
			}, _lifetime.Token).ConfigureAwait(false);
		}
		finally { await _writer.DisposeAsync().ConfigureAwait(false); }
	}

	public Task<int> Completion { get; }
	public ValueTask InputAsync(string text, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_host.EnqueueInput(text);
		return ValueTask.CompletedTask;
	}
	public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_host.UpdateWindowSize(columns, rows);
		return ValueTask.CompletedTask;
	}
	public ValueTask InterruptAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_host.Interrupt();
		return ValueTask.CompletedTask;
	}
	public async ValueTask DisposeAsync()
	{
		await _lifetime.CancelAsync().ConfigureAwait(false);
		_host.Complete();
#pragma warning disable VSTHRD003 // The hosted session has no UI-thread affinity.
		try { await Completion.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		finally
		{
			await _host.DisposeAsync().ConfigureAwait(false);
			await _scope.DisposeAsync().ConfigureAwait(false);
			_lifetime.Dispose();
		}
	}

	private sealed class InitialSize(int columns, int rows) : IWindowSizeProvider
	{
		public ValueTask<(int Width, int Height)?> GetSizeAsync(CancellationToken ct) => ValueTask.FromResult<(int, int)?>((columns, rows));
		public event EventHandler<WindowSizeEventArgs>? SizeChanged { add { } remove { } }
	}
}
