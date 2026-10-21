namespace Repl;

/// <summary>
/// Console I/O helpers: secret reading, countdown, prompt line reading.
/// </summary>
internal sealed partial class ConsoleInteractionChannel
{
	// What the user types at a prompt is echoed where the feedback routing sends the prompt's label, so an
	// answer never lands in a payload a program reads. A custom or capturing presenter may put the label
	// elsewhere.
	private TextWriter ResolvePromptEcho() => InteractionFeedbackRouting.Resolve(_outputOptions).Writer;

	private static async ValueTask<string?> ReadSecretLineAsync(char? mask, TextWriter echo, CancellationToken ct)
	{
		if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
		{
			return await ReadLineWithEscAsync(echo, ct).ConfigureAwait(false);
		}

		return await Task.Run(() => ReadSecretSync(mask, echo, ct), ct).ConfigureAwait(false);
	}

	private static string? ReadSecretSync(char? mask, TextWriter echo, CancellationToken ct)
	{
#pragma warning disable MA0045 // Intentionally synchronous — called via Task.Run from ReadSecretLineAsync
		ConsoleInputGate.Gate.Wait(ct);
#pragma warning restore MA0045
		try
		{
			return ReadSecretCore(mask, echo, ct);
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}

	private static string? ReadSecretCore(char? mask, TextWriter echo, CancellationToken ct)
	{
		var buffer = new System.Text.StringBuilder();
		while (!ct.IsCancellationRequested)
		{
			if (!Console.KeyAvailable)
			{
#pragma warning disable MA0045 // Intentionally synchronous — called via Task.Run from ReadSecretLineAsync
				Thread.Sleep(15);
#pragma warning restore MA0045
				continue;
			}

			var result = HandleSecretKey(buffer, mask, echo, ct);
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
		TextWriter echo,
		CancellationToken cancellationToken)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var timer = _timeProvider.CreateTimer(
			callback: static state =>
			{
#pragma warning disable MA0045 // Timer callback is non-async; Cancel is the only option here
				try { ((CancellationTokenSource)state!).Cancel(); }
				catch (ObjectDisposedException) { /* CTS disposed before timer fired. */ }
#pragma warning restore MA0045
			},
			state: timeoutCts, dueTime: timeout, period: Timeout.InfiniteTimeSpan);

		try
		{
			return await Task.Run(
					function: () => ReadSecretWithCountdownSync(timeout, mask, echo, timeoutCts.Token, cancellationToken),
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
		TextWriter echo,
		CancellationToken timeoutCt,
		CancellationToken externalCt)
	{
#pragma warning disable MA0045 // Intentionally synchronous — called via Task.Run from ReadSecretWithCountdownAsync
		ConsoleInputGate.Gate.Wait(externalCt);
#pragma warning restore MA0045
		try
		{
			return ReadSecretWithCountdownCore(timeout, mask, echo, timeoutCt, externalCt);
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}

	private static string? ReadSecretWithCountdownCore(
		TimeSpan timeout,
		char? mask,
		TextWriter echo,
		CancellationToken timeoutCt,
		CancellationToken externalCt)
	{
		var remaining = (int)Math.Ceiling(timeout.TotalSeconds);
		var buffer = new System.Text.StringBuilder();
		var lastSuffix = FormatCountdownSuffix(remaining, defaultLabel: null);
		var lastTickMs = Environment.TickCount64;
		var userTyping = false;

		Echo(echo, lastSuffix);

		while (!externalCt.IsCancellationRequested && (!timeoutCt.IsCancellationRequested || userTyping))
		{
			if (Console.KeyAvailable)
			{
				if (!userTyping)
				{
					userTyping = true;
					if (lastSuffix.Length > 0)
					{
						EraseInline(echo, lastSuffix.Length);
						lastSuffix = string.Empty;
					}
				}

				var result = HandleSecretKey(buffer, mask, echo, externalCt);
				if (result is not null)
				{
					return result;
				}

				continue;
			}

#pragma warning disable MA0045 // Intentionally synchronous — called via Task.Run from ReadSecretWithCountdownAsync
			Thread.Sleep(15);
#pragma warning restore MA0045

			if (!userTyping && remaining > 0)
			{
				(remaining, lastSuffix, lastTickMs) = TickCountdown(
					remaining, defaultLabel: null, lastSuffix, lastTickMs, echo);
			}
		}

		if (lastSuffix.Length > 0)
		{
			EraseInline(echo, lastSuffix.Length);
		}

		EchoLine(echo);
		return null;
	}

	private static string? HandleSecretKey(
		System.Text.StringBuilder buffer,
		char? mask,
		TextWriter echo,
		CancellationToken ct)
	{
		var key = Console.ReadKey(intercept: true);

		if (key.Key == ConsoleKey.Escape)
		{
			if (buffer.Length > 0 && mask is not null)
			{
				EraseInline(echo, buffer.Length);
			}

			throw new OperationCanceledException("Prompt cancelled via Esc.", ct);
		}

		if (key.Key == ConsoleKey.Enter)
		{
			EchoLine(echo);
			return buffer.ToString();
		}

		if (key.Key == ConsoleKey.Backspace)
		{
			if (buffer.Length > 0)
			{
				buffer.Remove(buffer.Length - 1, 1);
				if (mask is not null)
				{
					Echo(echo, "\b \b");
				}
			}

			return null;
		}

		if (key.KeyChar != '\0')
		{
			buffer.Append(key.KeyChar);
			if (mask is not null)
			{
				Echo(echo, mask.Value);
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
		var echo = ResolvePromptEcho();

		if (timeout is null || timeout.Value <= TimeSpan.Zero)
		{
			return await ReadLineWithEscAsync(echo, cancellationToken).ConfigureAwait(false);
		}

		if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
		{
			return await ReadWithTimeoutRedirectedAsync(timeout.Value, echo, cancellationToken)
				.ConfigureAwait(false);
		}

		return await ReadLineWithCountdownAsync(timeout.Value, defaultLabel, echo, cancellationToken)
			.ConfigureAwait(false);
	}

	private async ValueTask<string?> ReadWithTimeoutRedirectedAsync(
		TimeSpan timeout,
		TextWriter echo,
		CancellationToken cancellationToken)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var _ = _timeProvider.CreateTimer(
			static state =>
			{
#pragma warning disable MA0045 // Timer callback is non-async; Cancel is the only option here
				try { ((CancellationTokenSource)state!).Cancel(); }
				catch (ObjectDisposedException) { /* CTS disposed before timer fired. */ }
#pragma warning restore MA0045
			},
			timeoutCts, timeout, Timeout.InfiniteTimeSpan);
		try
		{
			return await ReadLineWithEscAsync(echo, timeoutCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return null;
		}
	}

	private async ValueTask<string?> ReadLineWithCountdownAsync(
		TimeSpan timeout,
		string? defaultLabel,
		TextWriter echo,
		CancellationToken cancellationToken)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var _ = _timeProvider.CreateTimer(
			static state =>
			{
#pragma warning disable MA0045 // Timer callback is non-async; Cancel is the only option here
				try { ((CancellationTokenSource)state!).Cancel(); }
				catch (ObjectDisposedException) { /* CTS disposed before timer fired. */ }
#pragma warning restore MA0045
			},
			timeoutCts, timeout, Timeout.InfiniteTimeSpan);

		var result = await Task.Run(
				() => ReadLineWithCountdownSync(timeout, defaultLabel, echo, timeoutCts.Token, cancellationToken),
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
		TextWriter echo,
		CancellationToken timeoutCt,
		CancellationToken externalCt)
	{
#pragma warning disable MA0045 // Intentionally synchronous — called via Task.Run from ReadLineWithCountdownAsync
		ConsoleInputGate.Gate.Wait(externalCt);
#pragma warning restore MA0045
		try
		{
			return ReadLineWithCountdownCore(timeout, defaultLabel, echo, timeoutCt, externalCt);
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}

	private static ConsoleLineReader.ReadResult ReadLineWithCountdownCore(
		TimeSpan timeout,
		string? defaultLabel,
		TextWriter echo,
		CancellationToken timeoutCt,
		CancellationToken externalCt)
	{
		var remaining = (int)Math.Ceiling(timeout.TotalSeconds);
		var buffer = new System.Text.StringBuilder();
		var lastSuffix = FormatCountdownSuffix(remaining, defaultLabel);
		var lastTickMs = Environment.TickCount64;
		var userTyping = false;

		Echo(echo, lastSuffix);

		while (!externalCt.IsCancellationRequested && (!timeoutCt.IsCancellationRequested || userTyping))
		{
			if (Console.KeyAvailable)
			{
				if (!userTyping)
				{
					userTyping = true;
					if (lastSuffix.Length > 0)
					{
						EraseInline(echo, lastSuffix.Length);
						lastSuffix = string.Empty;
					}
				}

				var result = HandleCountdownKey(buffer, echo);
				if (result is not null)
				{
					return result.Value;
				}

				continue;
			}

#pragma warning disable MA0045 // Intentionally synchronous — called via Task.Run from ReadLineWithCountdownAsync
			Thread.Sleep(15);
#pragma warning restore MA0045

			if (!userTyping && remaining > 0)
			{
				(remaining, lastSuffix, lastTickMs) = TickCountdown(
					remaining, defaultLabel, lastSuffix, lastTickMs, echo);
			}
		}

		if (lastSuffix.Length > 0)
		{
			EraseInline(echo, lastSuffix.Length);
		}

		EchoLine(echo);
		return new ConsoleLineReader.ReadResult(Line: null, Escaped: false);
	}

	private static ConsoleLineReader.ReadResult? HandleCountdownKey(
		System.Text.StringBuilder buffer,
		TextWriter echo)
	{
		var key = Console.ReadKey(intercept: true);

		if (key.Key == ConsoleKey.Escape)
		{
			if (buffer.Length > 0)
			{
				EraseInline(echo, buffer.Length);
			}

			return new ConsoleLineReader.ReadResult(Line: null, Escaped: true);
		}

		if (key.Key == ConsoleKey.Enter)
		{
			EchoLine(echo);
			return new ConsoleLineReader.ReadResult(buffer.ToString(), Escaped: false);
		}

		if (key.Key == ConsoleKey.Backspace)
		{
			if (buffer.Length > 0)
			{
				buffer.Remove(buffer.Length - 1, 1);
				Echo(echo, "\b \b");
			}

			return null;
		}

		if (key.KeyChar != '\0')
		{
			buffer.Append(key.KeyChar);
			Echo(echo, key.KeyChar);
		}

		return null;
	}

	private static (int Remaining, string Suffix, long LastTickMs) TickCountdown(
		int remaining,
		string? defaultLabel,
		string lastSuffix,
		long lastTickMs,
		TextWriter echo)
	{
		var now = Environment.TickCount64;
		if (now - lastTickMs < 1000)
		{
			return (remaining, lastSuffix, lastTickMs);
		}

		remaining--;
		EraseInline(echo, lastSuffix.Length);

		if (remaining > 0)
		{
			lastSuffix = FormatCountdownSuffix(remaining, defaultLabel);
			Echo(echo, lastSuffix);
		}
		else
		{
			lastSuffix = string.Empty;
		}

		return (remaining, lastSuffix, now);
	}

	private static void EraseInline(TextWriter echo, int length)
	{
		Echo(echo, new string('\b', length) + new string(' ', length) + new string('\b', length));
	}

	// The key-by-key loops run synchronously on a worker (Task.Run), polling the console, so they cannot await.
#pragma warning disable MA0045
	private static void Echo(TextWriter echo, string text) => echo.Write(text);

	private static void Echo(TextWriter echo, char character) => echo.Write(character);

	private static void EchoLine(TextWriter echo) => echo.WriteLine();
#pragma warning restore MA0045

	private static async ValueTask<string?> ReadLineWithEscAsync(TextWriter echo, CancellationToken ct)
	{
		var result = await ConsoleLineReader.ReadLineAsync(ct, echo).ConfigureAwait(false);
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
