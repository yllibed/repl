using System.IO.Pipelines;

namespace Repl.Telnet;

/// <summary>
/// Wraps a bidirectional <see cref="Stream"/> as an <see cref="IDuplexPipe"/>.
/// Suitable for TCP <see cref="System.Net.Sockets.NetworkStream"/>, named pipes, etc.
/// The caller retains ownership of the <see cref="Stream"/> lifetime.
/// </summary>
public sealed class StreamDuplexPipe : IDuplexPipe, IAsyncDisposable
{
	/// <summary>
	/// Creates a duplex pipe backed by the specified stream.
	/// </summary>
	public StreamDuplexPipe(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);
		Input = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
		Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
	}

	/// <inheritdoc />
	public PipeReader Input { get; }

	/// <inheritdoc />
	public PipeWriter Output { get; }

	/// <summary>
	/// Completes the pipe reader and writer. Does not close the underlying stream.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		await Input.CompleteAsync().ConfigureAwait(false);
		await Output.CompleteAsync().ConfigureAwait(false);
	}
}
