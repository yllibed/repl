namespace Repl;

/// <summary>
/// <see cref="IWindowSizeProvider"/> implementation for DTTERM in-band VT sequences.
/// Uses <see cref="VtProbe"/> for initial detection and receives pushed resize
/// notifications from <see cref="VtKeyReader"/> via <see cref="NotifyResize"/>.
/// </summary>
internal sealed class DttermWindowSizeProvider : IWindowSizeProvider
{
	private readonly IReplHost _host;

	public DttermWindowSizeProvider(IReplHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		_host = host;
	}

	/// <summary>
	/// Whether the VT probe detected ANSI support. Available after <see cref="GetSizeAsync"/>.
	/// </summary>
	internal bool? DetectedAnsiSupport { get; private set; }

	/// <inheritdoc />
	public event EventHandler<WindowSizeEventArgs>? SizeChanged;

	/// <inheritdoc />
	public async ValueTask<(int Width, int Height)?> GetSizeAsync(CancellationToken ct)
	{
		var probe = await VtProbe.DetectAsync(_host, ct).ConfigureAwait(false);
		DetectedAnsiSupport = probe.SupportsAnsi;

		if (probe.Width is not null && probe.Height is not null)
		{
			return (probe.Width.Value, probe.Height.Value);
		}

		return null;
	}

	/// <summary>
	/// Called by <see cref="VtKeyReader"/> when a DTTERM resize sequence is parsed.
	/// </summary>
	internal void NotifyResize(int width, int height)
	{
		if (width > 0 && height > 0)
		{
			SizeChanged?.Invoke(this, new WindowSizeEventArgs(width, height));
		}
	}
}
