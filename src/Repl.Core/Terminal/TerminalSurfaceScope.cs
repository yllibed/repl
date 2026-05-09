namespace Repl.Terminal;

internal sealed class TerminalSurfaceScope(TextWriter output, TerminalSurfaceMode mode) : IAsyncDisposable
{
	private bool _disposed;

	public TextWriter Output { get; } = output;

	public TerminalSurfaceMode Mode { get; } = mode;

	public ValueTask MoveHomeAsync() =>
		WriteAsync(AnsiSequences.CursorHome);

	public async ValueTask MoveCursorUpAsync(int rows)
	{
		if (rows <= 0)
		{
			return;
		}

		await Output.WriteAsync(AnsiSequences.CursorUp(rows)).ConfigureAwait(false);
	}

	public ValueTask MoveToColumnStartAsync() =>
		WriteAsync('\r');

	public ValueTask ClearToEndOfScreenAsync() =>
		WriteAsync(AnsiSequences.ClearToEndOfScreen);

	public ValueTask FlushAsync(CancellationToken cancellationToken) =>
		new(Output.FlushAsync(cancellationToken));

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		await RestoreAsync(Output, Mode).ConfigureAwait(false);
	}

	internal static async ValueTask RestoreAsync(TextWriter output, TerminalSurfaceMode mode)
	{
		await TryWriteAsync(output, AnsiSequences.EnableLineWrap).ConfigureAwait(false);
		await TryWriteAsync(output, AnsiSequences.ShowCursor).ConfigureAwait(false);
		if (mode == TerminalSurfaceMode.AlternateScreen)
		{
			await TryWriteAsync(output, AnsiSequences.LeaveAlternateScreen).ConfigureAwait(false);
		}

		try
		{
			await output.FlushAsync().ConfigureAwait(false);
		}
		catch (IOException)
		{
			// Best-effort terminal restore; output may be closed during process shutdown or pipe teardown.
		}
		catch (ObjectDisposedException)
		{
			// Best-effort terminal restore; output may already be disposed by the host.
		}
	}

	private ValueTask WriteAsync(string value) =>
		new(Output.WriteAsync(value));

	private ValueTask WriteAsync(char value) =>
		new(Output.WriteAsync(value));

	private static async ValueTask TryWriteAsync(TextWriter output, string value)
	{
		try
		{
			await output.WriteAsync(value).ConfigureAwait(false);
		}
		catch (IOException)
		{
			// Best-effort terminal write; output may be closed during process shutdown or pipe teardown.
		}
		catch (ObjectDisposedException)
		{
			// Best-effort terminal write; output may already be disposed by the host.
		}
	}
}
