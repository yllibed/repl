namespace Repl.IntegrationTests;

/// <summary>
/// Deterministic <see cref="IWindowSizeProvider"/> for tests: reports a fixed initial size
/// (no DTTERM VT probing, which would block on a terminal that never answers) and lets the
/// test push resize events manually.
/// </summary>
internal sealed class StaticWindowSizeProvider((int Width, int Height)? initialSize = null) : IWindowSizeProvider
{
	private readonly (int Width, int Height)? _initialSize = initialSize;

	public event EventHandler<WindowSizeEventArgs>? SizeChanged;

	public ValueTask<(int Width, int Height)?> GetSizeAsync(CancellationToken cancellationToken) =>
		ValueTask.FromResult(_initialSize);

	public void Push(int width, int height) =>
		SizeChanged?.Invoke(this, new WindowSizeEventArgs(width, height));
}
