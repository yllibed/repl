using System.Text;

using Microsoft.AspNetCore.SignalR;

namespace HostingRemoteSample;

/// <summary>
/// A <see cref="TextWriter"/> that sends text to a SignalR client via <see cref="ISingleClientProxy"/>.
/// Synchronous <see cref="Write(string?)"/> calls are buffered and sent on <see cref="FlushAsync()"/>.
/// </summary>
internal sealed class SignalRTextWriter(ISingleClientProxy caller) : TextWriter
{
	private readonly Lock _lock = new();
	private readonly StringBuilder _buffer = new();

	public override Encoding Encoding => Encoding.UTF8;

	public override void Write(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return;
		}

		lock (_lock) { _buffer.Append(value); }
	}

	public override void Write(char value)
	{
		lock (_lock) { _buffer.Append(value); }
	}

	public override void WriteLine(string? value)
	{
		lock (_lock)
		{
			_buffer.Append(value ?? string.Empty);
			_buffer.Append(Environment.NewLine);
		}
	}

	public override async Task WriteAsync(string? value)
	{
		await FlushBufferAsync().ConfigureAwait(false);
		if (string.IsNullOrEmpty(value))
		{
			return;
		}

		await caller.SendAsync("Output", value).ConfigureAwait(false);
	}

	public override Task WriteLineAsync(string? value) =>
		WriteAsync((value ?? string.Empty) + Environment.NewLine);

	public override Task FlushAsync() => FlushBufferAsync();

	private Task FlushBufferAsync()
	{
		string? text;
		lock (_lock)
		{
			if (_buffer.Length == 0)
			{
				return Task.CompletedTask;
			}

			text = _buffer.ToString();
			_buffer.Clear();
		}

		return caller.SendAsync("Output", text);
	}
}
