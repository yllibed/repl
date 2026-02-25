using System.Text;
using System.Threading.Channels;

namespace Repl;

/// <summary>
/// A <see cref="TextReader"/> backed by a <see cref="Channel{T}"/> that supports both
/// char-level reads (<see cref="ReadAsync(Memory{char}, CancellationToken)"/>) and
/// line-level reads (<see cref="ReadLineAsync(CancellationToken)"/>).
/// Transport code pushes raw text via <see cref="Enqueue"/>; the reader handles
/// internal buffering and newline parsing.
/// </summary>
public sealed class ChannelTextReader : TextReader
{
	private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
		new UnboundedChannelOptions { SingleReader = true });

	private ReadOnlyMemory<char> _remainder;

	/// <summary>
	/// Pushes a raw text chunk from the transport layer.
	/// </summary>
	/// <param name="text">Text to enqueue.</param>
	public void Enqueue(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}

		_channel.Writer.TryWrite(text);
	}

	/// <summary>
	/// Signals that no more input will arrive (EOF).
	/// </summary>
	public void Complete() => _channel.Writer.TryComplete();

	/// <summary>
	/// Reads characters into <paramref name="buffer"/>, returning the number of characters read.
	/// Returns 0 when the channel is completed and no buffered data remains.
	/// This method is used by <see cref="VtProbe"/> for char-level terminal detection.
	/// </summary>
	public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
	{
		// Drain remainder first.
		if (_remainder.Length > 0)
		{
			return ConsumeRemainder(buffer);
		}

		// Wait for the next chunk from the channel.
		if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return 0; // EOF
		}

		if (!_channel.Reader.TryRead(out var chunk))
		{
			return 0;
		}

		_remainder = chunk.AsMemory();
		return ConsumeRemainder(buffer);
	}

	/// <summary>
	/// Reads a complete line, accumulating across chunks until a line terminator is found.
	/// Returns <c>null</c> when the channel is completed and no buffered data remains.
	/// </summary>
	public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
	{
		var sb = new StringBuilder();

		while (true)
		{
			// Try to extract a line from the current remainder.
			if (_remainder.Length > 0)
			{
				var span = _remainder.Span;
				for (var i = 0; i < span.Length; i++)
				{
					var ch = span[i];
					if (ch is not ('\r' or '\n'))
					{
						continue;
					}

					// Found a line terminator.
					sb.Append(span[..i]);
					var consumed = i + 1;

					// Handle CRLF.
					if (ch == '\r' && consumed < span.Length && span[consumed] == '\n')
					{
						consumed++;
					}

					_remainder = _remainder[consumed..];
					return sb.ToString();
				}

				// No terminator found — append all and continue.
				sb.Append(span);
				_remainder = default;
			}

			// Fetch the next chunk.
			if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
			{
				// EOF: return accumulated text or null.
				return sb.Length > 0 ? sb.ToString() : null;
			}

			if (!_channel.Reader.TryRead(out var chunk))
			{
				return sb.Length > 0 ? sb.ToString() : null;
			}

			_remainder = chunk.AsMemory();
		}
	}

	private int ConsumeRemainder(Memory<char> buffer)
	{
		var toCopy = Math.Min(_remainder.Length, buffer.Length);
		_remainder[..toCopy].CopyTo(buffer);
		_remainder = _remainder[toCopy..];
		return toCopy;
	}
}
