using System.Text;

namespace Repl.Remote;

internal sealed class SystemRemoteConsole : IRemoteConsole
{
	public IDisposable Enter()
	{
		if (Console.IsInputRedirected || Console.IsOutputRedirected) { throw new IOException("Interactive mode requires a console. Use exec for redirected input or output."); }
		var original = Console.TreatControlCAsInput;
		var outputMode = OperatingSystem.IsWindows() ? WindowsConsoleMode.Enter() : null;
		try
		{
			Console.TreatControlCAsInput = true;
			return new ConsoleState(original, outputMode);
		}
		catch { outputMode?.Dispose(); throw; }
	}

	public (int Columns, int Rows) Size => (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight));
	public async ValueTask<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken)
	{
		if (Console.KeyAvailable) { return Console.ReadKey(intercept: true); }
		await Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken).ConfigureAwait(false);
		return null;
	}
	public async ValueTask WriteAsync(string text, CancellationToken cancellationToken)
	{
		var output = Console.OpenStandardOutput();
		await output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
		await output.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private sealed class ConsoleState(bool treatControlCAsInput, IDisposable? outputMode) : IDisposable
	{
		public void Dispose()
		{
			try { Console.TreatControlCAsInput = treatControlCAsInput; }
			finally { outputMode?.Dispose(); }
		}
	}
}
