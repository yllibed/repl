namespace Repl.Telnet;

/// <summary>
/// <see cref="IWindowSizeProvider"/> that adapts Telnet NAWS (RFC 1073)
/// subnegotiation events from <see cref="TelnetFraming"/> into the pluggable interface.
/// The TCS is initialized eagerly so that NAWS arriving before <see cref="GetSizeAsync"/>
/// is called (e.g. during framing negotiation) is not lost.
/// </summary>
public sealed class NawsWindowSizeProvider : IWindowSizeProvider
{
	private readonly TaskCompletionSource<(int Width, int Height)> _firstSize =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>
	/// Creates a new NAWS window size provider backed by the specified Telnet framing layer.
	/// </summary>
	public NawsWindowSizeProvider(TelnetFraming framing)
	{
		ArgumentNullException.ThrowIfNull(framing);
		framing.WindowSizeChanged += OnFramingWindowSizeChanged;
	}

	/// <inheritdoc />
	public event EventHandler<WindowSizeEventArgs>? SizeChanged;

	/// <inheritdoc />
	public async ValueTask<(int Width, int Height)?> GetSizeAsync(CancellationToken ct)
	{
		try
		{
			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeoutCts.CancelAfter(2000);
			return await _firstSize.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			// Timeout waiting for NAWS — size unknown.
			return null;
		}
	}

	private void OnFramingWindowSizeChanged(object? sender, WindowSizeEventArgs e)
	{
		_firstSize.TrySetResult((e.Width, e.Height));
		SizeChanged?.Invoke(this, e);
	}
}
