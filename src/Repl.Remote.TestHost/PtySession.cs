using System.Text;
using System.Threading.Channels;
using Porta.Pty;

namespace Repl.Remote.TestHost;

internal sealed class PtySession : IAsyncDisposable
{
	private readonly IPtyConnection _connection;
	private readonly CancellationTokenSource _reading = new();
	private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
	private readonly Task _readTask;
	private string _pending = string.Empty;

	private PtySession(IPtyConnection connection)
	{
		_connection = connection;
		_readTask = ReadOutputAsync();
	}

	internal static async Task<PtySession> StartAsync(string executable, string[] arguments, CancellationToken ct)
	{
		var connection = await PtyProvider.SpawnAsync(new PtyOptions
		{
			App = OperatingSystem.IsWindows() ? Path.ChangeExtension(executable, ".exe") : executable,
			CommandLine = arguments,
			Cwd = Environment.CurrentDirectory,
			Cols = 80,
			Rows = 24,
			Name = "xterm-256color",
			Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["TERM"] = "xterm-256color" },
			UseAsyncIo = true,
		}, ct).ConfigureAwait(false);
		return new PtySession(connection);
	}

	internal async Task SendAsync(string text, CancellationToken ct)
	{
		await _connection.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
		await _connection.WriterStream.FlushAsync(ct).ConfigureAwait(false);
	}

	internal void Resize(int columns, int rows) => _connection.Resize(columns, rows);

	internal async Task ExpectAsync(string marker, CancellationToken ct)
	{
		try
		{
			while (!_pending.Contains(marker, StringComparison.Ordinal))
			{
				_pending += await _output.Reader.ReadAsync(ct).ConfigureAwait(false);
			}
			_pending = _pending[(_pending.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
		}
		catch (Exception error) when (error is OperationCanceledException or ChannelClosedException)
		{
			throw new InvalidOperationException($"Missing terminal output '{marker}'. Received: {_pending}", error);
		}
	}

	internal async Task<int> WaitForExitAsync(CancellationToken ct)
	{
		while (!_connection.WaitForExit(0))
		{
			await Task.Delay(TimeSpan.FromMilliseconds(25), TimeProvider.System, ct).ConfigureAwait(false);
		}
		return _connection.ExitCode;
	}

	public async ValueTask DisposeAsync()
	{
		await _reading.CancelAsync().ConfigureAwait(false);
		try
		{
			if (!_connection.WaitForExit(0)) { _connection.Kill(); }
		}
		finally
		{
			_connection.Dispose();
#pragma warning disable VSTHRD003 // The reader belongs to this PTY and must finish before its cancellation source is disposed.
			await _readTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			_reading.Dispose();
		}
	}

	private async Task ReadOutputAsync()
	{
		using var reader = new StreamReader(_connection.ReaderStream, Encoding.UTF8, leaveOpen: true);
		var buffer = new char[4096];
		try
		{
			while (true)
			{
				var count = await reader.ReadAsync(buffer.AsMemory(), _reading.Token).ConfigureAwait(false);
				if (count == 0) { break; }
				_output.Writer.TryWrite(new string(buffer, 0, count));
			}
		}
		catch (OperationCanceledException) when (_reading.IsCancellationRequested) { }
		catch (IOException error)
		{
			// Unix PTYs can report EIO when their last slave closes. Preserve it if an expectation still needs output.
			_output.Writer.TryComplete(error);
		}
		finally { _output.Writer.TryComplete(); }
	}
}
