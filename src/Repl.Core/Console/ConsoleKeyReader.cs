namespace Repl;

/// <summary>
/// Console-based implementation of <see cref="IReplKeyReader"/>.
/// Acquires the shared <see cref="ConsoleInputGate"/> while reading keys.
/// </summary>
internal sealed class ConsoleKeyReader : IReplKeyReader
{
	public bool KeyAvailable =>
		!Console.IsInputRedirected && !ReplSessionIO.IsSessionActive && Console.KeyAvailable;

	public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct)
	{
		if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
		{
			// When redirected, wait for cancellation — no keys will arrive.
			await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
			throw new InvalidOperationException("Console input is redirected.");
		}

		return await Task.Run(() => ReadKeySync(ct), ct).ConfigureAwait(false);
	}

	private static ConsoleKeyInfo ReadKeySync(CancellationToken ct)
	{
#pragma warning disable MA0045 // Intentionally synchronous — called via Task.Run from ReadKeyAsync
		ConsoleInputGate.Gate.Wait(ct);
#pragma warning restore MA0045
		try
		{
			while (true)
			{
				ct.ThrowIfCancellationRequested();

				if (Console.KeyAvailable)
				{
					return Console.ReadKey(intercept: true);
				}

#pragma warning disable MA0045 // Intentionally synchronous — called via Task.Run from ReadKeyAsync
				Thread.Sleep(15);
#pragma warning restore MA0045
			}
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}
}
