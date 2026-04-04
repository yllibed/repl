namespace Repl;

/// <summary>
/// Console I/O helpers: secret reading, countdown, prompt line reading.
/// </summary>
internal sealed partial class ConsoleInteractionChannel
{
	private static async ValueTask<string?> ReadSecretLineAsync(char? mask, CancellationToken ct)
	{
		if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
		{
			return await ReadLineWithEscAsync(ct).ConfigureAwait(false);
		}

		return await Task.Run(() => ReadSecretSync(mask, ct), ct).ConfigureAwait(false);
	}

	private static string? ReadSecretSync(char? mask, CancellationToken ct)
	{
		ConsoleInputGate.Gate.Wait(ct);
		try
		{
			return ReadSecretCore(mask, ct);
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}

	private static string? ReadSecretCore(char? mask, CancellationToken ct)
	{
		var buffer = new System.Text.StringBuilder();
		while (!ct.IsCancellationRequested)
		{
			if (!Console.KeyAvailable)
			{
				Thread.Sleep(15);
				continue;
			}

			var result = HandleSecretKey(buffer, mask, ct);
			if (result is not null)
			{
				return result;
			}
		}

		return null;
	}

	private async ValueTask<string?> ReadSecretWithCountdownAsync(
		TimeSpan timeout,
		char? mask,
		CancellationToken cancellationToken)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var timer = _timeProvider.CreateTimer(
			callback: static state =>
			{
				try { ((CancellationTokenSource)state!).Cancel(); }
				catch (ObjectDisposedException) { /* CTS disposed before timer fired. */ }
			},
			state: timeoutCts, dueTime: timeout, period: Timeout.InfiniteTimeSpan);

		try
		{
			return await Task.Run(
					function: () => ReadSecretWithCountdownSync(timeout, mask, timeoutCts.Token, cancellationToken),
					cancellationToken: cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return null;
		}
	}

	private static string? ReadSecretWithCountdownSync(
		TimeSpan timeout,
		char? mask,
		CancellationToken timeoutCt,
		CancellationToken externalCt)
	{
		ConsoleInputGate.Gate.Wait(externalCt);
		try
		{
			return ReadSecretWithCountdownCore(timeout, mask, timeoutCt, externalCt);
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}

	private static string? ReadSecretWithCountdownCore(
		TimeSpan timeout,
		char? mask,
		CancellationToken timeoutCt,
		CancellationToken externalCt)
	{
		var remaining = (int)Math.Ceiling(timeout.TotalSeconds);
		var buffer = new System.Text.StringBuilder();
		var lastSuffix = FormatCountdownSuffix(remaining, defaultLabel: null);
		var lastTickMs = Environment.TickCount64;
		var userTyping = false;

		Console.Write(lastSuffix);

		while (!externalCt.IsCancellationRequested && (!timeoutCt.IsCancellationRequested || userTyping))
		{
			if (Console.KeyAvailable)
			{
				if (!userTyping)
				{
					userTyping = true;
					if (lastSuffix.Length > 0)
					{
						EraseInline(lastSuffix.Length);
						lastSuffix = string.Empty;
					}
				}

				var result = HandleSecretKey(buffer, mask, externalCt);
				if (result is not null)
				{
					return result;
				}

				continue;
			}

			Thread.Sleep(15);

			if (!userTyping && remaining > 0)
			{
				(remaining, lastSuffix, lastTickMs) = TickCountdown(
					remaining, defaultLabel: null, lastSuffix, lastTickMs);
			}
		}

		if (lastSuffix.Length > 0)
		{
			EraseInline(lastSuffix.Length);
		}

		Console.WriteLine();
		return null;
	}

	private static string? HandleSecretKey(
		System.Text.StringBuilder buffer,
		char? mask,
		CancellationToken ct)
	{
		var key = Console.ReadKey(intercept: true);

		if (key.Key == ConsoleKey.Escape)
		{
			if (buffer.Length > 0 && mask is not null)
			{
				EraseInline(buffer.Length);
			}

			throw new OperationCanceledException("Prompt cancelled via Esc.", ct);
		}

		if (key.Key == ConsoleKey.Enter)
		{
			Console.WriteLine();
			return buffer.ToString();
		}

		if (key.Key == ConsoleKey.Backspace)
		{
			if (buffer.Length > 0)
			{
				buffer.Remove(buffer.Length - 1, 1);
				if (mask is not null)
				{
					Console.Write("\b \b");
				}
			}

			return null;
		}

		if (key.KeyChar != '\0')
		{
			buffer.Append(key.KeyChar);
			if (mask is not null)
			{
				Console.Write(mask.Value);
			}
		}

		return null;
	}

	private async ValueTask<string?> ReadPromptLineAsync(
		string name,
		string prompt,
		string kind,
		CancellationToken cancellationToken,
		TimeSpan? timeout = null,
		string? defaultLabel = null)
	{
		await _presenter.PresentAsync(
				new ReplPromptEvent(name, prompt, kind),
				cancellationToken)
			.ConfigureAwait(false);

		if (timeout is null || timeout.Value <= TimeSpan.Zero)
		{
			return await ReadLineWithEscAsync(cancellationToken).ConfigureAwait(false);
		}

		if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
		{
			return await ReadWithTimeoutRedirectedAsync(cancellationToken, timeout.Value)
				.ConfigureAwait(false);
		}

		return await ReadLineWithCountdownAsync(timeout.Value, defaultLabel, cancellationToken)
			.ConfigureAwait(false);
	}

	private async ValueTask<string?> ReadWithTimeoutRedirectedAsync(
		CancellationToken cancellationToken,
		TimeSpan timeout)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var _ = _timeProvider.CreateTimer(
			static state =>
			{
				try { ((CancellationTokenSource)state!).Cancel(); }
				catch (ObjectDisposedException) { /* CTS disposed before timer fired. */ }
			},
			timeoutCts, timeout, Timeout.InfiniteTimeSpan);
		try
		{
			return await ReadLineWithEscAsync(timeoutCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return null;
		}
	}

	private async ValueTask<string?> ReadLineWithCountdownAsync(
		TimeSpan timeout,
		string? defaultLabel,
		CancellationToken cancellationToken)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var _ = _timeProvider.CreateTimer(
			static state =>
			{
				try { ((CancellationTokenSource)state!).Cancel(); }
				catch (ObjectDisposedException) { /* CTS disposed before timer fired. */ }
			},
			timeoutCts, timeout, Timeout.InfiniteTimeSpan);

		var result = await Task.Run(
				() => ReadLineWithCountdownSync(timeout, defaultLabel, timeoutCts.Token, cancellationToken),
				cancellationToken)
			.ConfigureAwait(false);

		if (result.Escaped)
		{
			throw new OperationCanceledException("Prompt cancelled via Esc.", cancellationToken);
		}

		return result.Line;
	}

	/// <summary>
	/// Combined countdown + key reading loop. The countdown suffix is displayed
	/// while the user hasn't typed anything. As soon as the first key arrives,
	/// the suffix is erased and normal key-by-key reading takes over.
	/// </summary>
	private static ConsoleLineReader.ReadResult ReadLineWithCountdownSync(
		TimeSpan timeout,
		string? defaultLabel,
		CancellationToken timeoutCt,
		CancellationToken externalCt)
	{
		ConsoleInputGate.Gate.Wait(externalCt);
		try
		{
			return ReadLineWithCountdownCore(timeout, defaultLabel, timeoutCt, externalCt);
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}

	private static ConsoleLineReader.ReadResult ReadLineWithCountdownCore(
		TimeSpan timeout,
		string? defaultLabel,
		CancellationToken timeoutCt,
		CancellationToken externalCt)
	{
		var remaining = (int)Math.Ceiling(timeout.TotalSeconds);
		var buffer = new System.Text.StringBuilder();
		var lastSuffix = FormatCountdownSuffix(remaining, defaultLabel);
		var lastTickMs = Environment.TickCount64;
		var userTyping = false;

		Console.Write(lastSuffix);

		while (!externalCt.IsCancellationRequested && (!timeoutCt.IsCancellationRequested || userTyping))
		{
			if (Console.KeyAvailable)
			{
				if (!userTyping)
				{
					userTyping = true;
					if (lastSuffix.Length > 0)
					{
						EraseInline(lastSuffix.Length);
						lastSuffix = string.Empty;
					}
				}

				var result = HandleCountdownKey(buffer);
				if (result is not null)
				{
					return result.Value;
				}

				continue;
			}

			Thread.Sleep(15);

			if (!userTyping && remaining > 0)
			{
				(remaining, lastSuffix, lastTickMs) = TickCountdown(
					remaining, defaultLabel, lastSuffix, lastTickMs);
			}
		}

		if (lastSuffix.Length > 0)
		{
			EraseInline(lastSuffix.Length);
		}

		Console.WriteLine();
		return new ConsoleLineReader.ReadResult(Line: null, Escaped: false);
	}

	private static ConsoleLineReader.ReadResult? HandleCountdownKey(
		System.Text.StringBuilder buffer)
	{
		var key = Console.ReadKey(intercept: true);

		if (key.Key == ConsoleKey.Escape)
		{
			if (buffer.Length > 0)
			{
				EraseInline(buffer.Length);
			}

			return new ConsoleLineReader.ReadResult(Line: null, Escaped: true);
		}

		if (key.Key == ConsoleKey.Enter)
		{
			Console.WriteLine();
			return new ConsoleLineReader.ReadResult(buffer.ToString(), Escaped: false);
		}

		if (key.Key == ConsoleKey.Backspace)
		{
			if (buffer.Length > 0)
			{
				buffer.Remove(buffer.Length - 1, 1);
				Console.Write("\b \b");
			}

			return null;
		}

		if (key.KeyChar != '\0')
		{
			buffer.Append(key.KeyChar);
			Console.Write(key.KeyChar);
		}

		return null;
	}

	private static (int Remaining, string Suffix, long LastTickMs) TickCountdown(
		int remaining,
		string? defaultLabel,
		string lastSuffix,
		long lastTickMs)
	{
		var now = Environment.TickCount64;
		if (now - lastTickMs < 1000)
		{
			return (remaining, lastSuffix, lastTickMs);
		}

		remaining--;
		EraseInline(lastSuffix.Length);

		if (remaining > 0)
		{
			lastSuffix = FormatCountdownSuffix(remaining, defaultLabel);
			Console.Write(lastSuffix);
		}
		else
		{
			lastSuffix = string.Empty;
		}

		return (remaining, lastSuffix, now);
	}

	private static void EraseInline(int length)
	{
		Console.Write(new string('\b', length) + new string(' ', length) + new string('\b', length));
	}

	private static async ValueTask<string?> ReadLineWithEscAsync(CancellationToken ct)
	{
		var result = await ConsoleLineReader.ReadLineAsync(ct).ConfigureAwait(false);
		if (result.Escaped)
		{
			throw new OperationCanceledException("Prompt cancelled via Esc.", ct);
		}

		return result.Line;
	}

	/// <summary>
	/// Formats the inline countdown suffix shown next to a prompt (e.g. " (10s -> Skip)").
	/// </summary>
	internal static string FormatCountdownSuffix(int remainingSeconds, string? defaultLabel) =>
		string.IsNullOrWhiteSpace(defaultLabel)
			? $" ({remainingSeconds}s)"
			: $" ({remainingSeconds}s -> {defaultLabel})";
}
