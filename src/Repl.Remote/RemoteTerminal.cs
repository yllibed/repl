using System.Threading.Channels;
using StreamJsonRpc;

namespace Repl.Remote;

/// <summary>One terminal connection. Output is UTF-8 text containing VT sequences.</summary>
public sealed class RemoteTerminal : IAsyncDisposable
{
	private readonly JsonRpc _rpc;
	private readonly Func<Exception, ValueTask> _disconnect;
	private readonly Channel<string> _output = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
	{
		SingleReader = true,
		SingleWriter = true,
	});
	private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	internal RemoteTerminal(JsonRpc rpc, Func<Exception, ValueTask> disconnect) { _rpc = rpc; _disconnect = disconnect; }
	internal string SessionId { get; } = Guid.NewGuid().ToString("N");
	/// <summary>Gets terminal output in arrival order. A slow consumer disconnects rather than losing text.</summary>
	public ChannelReader<string> Output => _output.Reader;
	/// <summary>Gets the remote exit code, or a transport failure if the connection was lost.</summary>
	public Task<int> Completion => _completion.Task;
	/// <summary>Sends text or VT input.</summary>
	public Task InputAsync(string text, CancellationToken cancellationToken = default) =>
		_rpc.InvokeWithParameterObjectAsync("terminal.input", new { sessionId = SessionId, text }, cancellationToken);
	/// <summary>Changes the remote terminal size.</summary>
	public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) =>
		_rpc.InvokeWithParameterObjectAsync("terminal.resize", new { sessionId = SessionId, columns, rows }, cancellationToken);
	/// <summary>Interrupts the remote command.</summary>
	public Task InterruptAsync(CancellationToken cancellationToken = default) =>
		_rpc.InvokeWithParameterObjectAsync("terminal.interrupt", new { sessionId = SessionId }, cancellationToken);
	internal ValueTask AcceptOutputAsync(string text)
	{
		if (!_output.Writer.TryWrite(text))
		{
			return _disconnect(new IOException("Terminal output exceeded the client buffer. Consume Output while the terminal is open."));
		}
		return ValueTask.CompletedTask;
	}
	internal void Finish(int exitCode) { _output.Writer.TryComplete(); _completion.TrySetResult(exitCode); }
	internal void Finish(Exception error) { _output.Writer.TryComplete(error); _completion.TrySetException(error); }
	/// <summary>Closes the remote session without stopping the server process.</summary>
	public async ValueTask DisposeAsync()
	{
		if (!_completion.Task.IsCompleted)
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			try
			{
				await _rpc.InvokeWithParameterObjectAsync("terminal.close", new { sessionId = SessionId }, timeout.Token)
					.WaitAsync(timeout.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
			{
				var failure = new TimeoutException("The remote terminal did not close within five seconds; the connection was disconnected.", error);
				await _disconnect(failure).ConfigureAwait(false);
				throw failure;
			}
		}
	}
}
