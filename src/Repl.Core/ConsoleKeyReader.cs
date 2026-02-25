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
		ConsoleInputGate.Gate.Wait(ct);
		try
		{
			while (true)
			{
				ct.ThrowIfCancellationRequested();

				if (Console.KeyAvailable)
				{
					return Console.ReadKey(intercept: true);
				}

				Thread.Sleep(15);
			}
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}
}
