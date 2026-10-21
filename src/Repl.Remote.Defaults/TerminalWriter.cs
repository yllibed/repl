using System.Text;
using System.Threading.Channels;

namespace Repl.Remote;

// Repl's line editor also writes synchronously. A single pump preserves its VT writes alongside async command output.
internal sealed class TerminalWriter : TextWriter
{
	private readonly Channel<string> _queue = Channel.CreateBounded<string>(256);
	private readonly Task _pump;
	private readonly CancellationTokenSource _lifetime;

	internal TerminalWriter(Func<string, CancellationToken, ValueTask> write, CancellationTokenSource lifetime)
	{
		_lifetime = lifetime;
		_pump = PumpAsync(write);
	}

	public override Encoding Encoding => Encoding.UTF8;
	public override void Write(char value) => Write(value.ToString());
	public override void Write(string? value)
	{
		if (value is not null && !_queue.Writer.TryWrite(value)) { throw new IOException("Terminal output buffer is full."); }
	}
	public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
	public override Task WriteAsync(string? value) => value is null ? Task.CompletedTask : _queue.Writer.WriteAsync(value, _lifetime.Token).AsTask();
	public override Task WriteAsync(char value) => WriteAsync(value.ToString());
	public override Task WriteAsync(char[] buffer, int index, int count) => WriteAsync(new string(buffer, index, count));
	public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
		_queue.Writer.WriteAsync(buffer.ToString(), cancellationToken).AsTask();
	public override Task WriteLineAsync(string? value) => WriteAsync(value + NewLine);
	public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) => WriteAsync(buffer.ToString() + NewLine);

	private async Task PumpAsync(Func<string, CancellationToken, ValueTask> write)
	{
		try
		{
			char? pendingSurrogate = null;
			await foreach (var text in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
			{
				var chunk = pendingSurrogate is { } high ? high + text : text;
				pendingSurrogate = null;
				if (chunk.Length > 0 && char.IsHighSurrogate(chunk[^1]))
				{
					pendingSurrogate = chunk[^1];
					chunk = chunk[..^1];
				}
				if (chunk.Length > 0) { await write(chunk, _lifetime.Token).ConfigureAwait(false); }
			}
			if (pendingSurrogate is not null) { await write("\ufffd", _lifetime.Token).ConfigureAwait(false); }
		}
		finally { await _lifetime.CancelAsync().ConfigureAwait(false); }
	}

	public override async ValueTask DisposeAsync()
	{
		_queue.Writer.TryComplete();
#pragma warning disable VSTHRD003 // The transport output pump never requires a UI context.
		await _pump.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		GC.SuppressFinalize(this);
	}
}
