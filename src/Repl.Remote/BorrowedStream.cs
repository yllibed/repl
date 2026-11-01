namespace Repl.Remote;

internal sealed class BorrowedStream(Stream stream) : Stream
{
	public override bool CanRead => stream.CanRead;
	public override bool CanSeek => stream.CanSeek;
	public override bool CanWrite => stream.CanWrite;
	public override long Length => stream.Length;
	public override long Position { get => stream.Position; set => stream.Position = value; }
	public override void Flush() => stream.Flush();
	public override Task FlushAsync(CancellationToken cancellationToken) => stream.FlushAsync(cancellationToken);
	public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		stream.ReadAsync(buffer, offset, count, cancellationToken);
	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
	public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
	public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		stream.WriteAsync(buffer, offset, count, cancellationToken);
	public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => stream.WriteAsync(buffer, cancellationToken);
	public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
	public override void SetLength(long value) => stream.SetLength(value);
	protected override void Dispose(bool disposing) { }
}
