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

	// untilFirstKey is ct, or a timed prompt's deadline, which only applies until typing starts in a hosted terminal.
	private static async ValueTask<string?> ReadSecretLineAsync(
		char? mask, TextWriter echo, CancellationToken untilFirstKey, CancellationToken ct)
	{
		if (ReplSessionIO.KeyReader is { } keyReader)
		{
			return await ReadSecretRemoteAsync(keyReader, mask, echo, untilFirstKey, ct).ConfigureAwait(false);
		}

		if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
		{
			return await ReadLineWithEscAsync(echo, untilFirstKey).ConfigureAwait(false);
		}

		return await Task.Run(() => ReadSecretSync(mask, echo, ct), ct).ConfigureAwait(false);
	}

	// A hosted terminal's keys, under the same rule as the console's (ApplySecretKey).
	private static async ValueTask<string?> ReadSecretRemoteAsync(
		IReplKeyReader keyReader, char? mask, TextWriter echo, CancellationToken untilFirstKey, CancellationToken ct)
	{
		var buffer = new System.Text.StringBuilder();
		var token = untilFirstKey;
		while (true)
		{
			var key = await ReadRemoteKeyAsync(keyReader, token).ConfigureAwait(false);
			// Once typing starts the deadline no longer applies, as at the console: cancelling mid-entry would leave
			// the rest of the secret for the next reader, which echoes in clear. A terminal report, such as a resize,
			// carries no character and is not typing.
			if (key.KeyChar != '\0' || key.Key is ConsoleKey.Escape or ConsoleKey.Enter or ConsoleKey.Backspace)
			{
				token = ct;
			}
			var (outcome, text) = ApplySecretKey(key, buffer, mask);
			// A remote terminal needs a carriage return to go back to column 1, as the line editor writes it.
			var output = outcome == SecretKeyOutcome.Submit ? "\r\n" : text;
			await ConsoleLineReader.WriteEchoAsync(echo, output, settled: outcome != SecretKeyOutcome.Continue, token)
				.ConfigureAwait(false);

			switch (outcome)
			{
				case SecretKeyOutcome.Cancel:
					throw new PromptAbandonedException("Prompt cancelled via Esc.", token);
				case SecretKeyOutcome.Submit:
					return buffer.ToString();
			}
		}
	}

	// Whichever reader it is: ct's cancellation, however the reader observed it, is reported as ct's
	// (PromptCancellation); a reader that stops otherwise has lost its input.
	private static async ValueTask<ConsoleKeyInfo> ReadRemoteKeyAsync(IReplKeyReader keyReader, CancellationToken ct)
	{
		try
		{
			return await keyReader.ReadKeyAsync(ct).ConfigureAwait(false);
		}
		catch (OperationCanceledException ex) when (ex is not PromptAbandonedException
			&& PromptCancellation.IsThroughAnotherToken(ex, ct))
		{
			throw PromptCancellation.AsCancellationOf(ex, ct);
		}
		catch (OperationCanceledException ex) when (ex is not PromptAbandonedException
			&& (ex.CancellationToken != ct || !ct.IsCancellationRequested))
		{
			throw new PromptAbandonedException("The terminal input ended.", ct, ex);
		}
	}

	private enum SecretKeyOutcome
	{
		Continue,
		Submit,
		Cancel,
	}

	// The one rule for a key at a secret prompt, at the console and in a hosted terminal alike: the buffer takes
	// what is typed, and the text returned for the caller to echo only ever shows or erases the mask.
	private static (SecretKeyOutcome Outcome, string Echo) ApplySecretKey(
		ConsoleKeyInfo key, System.Text.StringBuilder buffer, char? mask)
	{
		switch (key.Key)
		{
			case ConsoleKey.Escape:
				return (SecretKeyOutcome.Cancel, buffer.Length > 0 && mask is not null ? EraseSequence(buffer.Length) : string.Empty);
			case ConsoleKey.Enter:
				return (SecretKeyOutcome.Submit, string.Empty);
			case ConsoleKey.Backspace:
				if (buffer.Length == 0)
				{
					return (SecretKeyOutcome.Continue, string.Empty);
				}

				buffer.Remove(buffer.Length - 1, 1);
				return (SecretKeyOutcome.Continue, mask is null ? string.Empty : "\b \b");
			default:
				if (key.KeyChar == '\0')
				{
					return (SecretKeyOutcome.Continue, string.Empty);
				}

				buffer.Append(key.KeyChar);
				return (SecretKeyOutcome.Continue, mask is { } shown ? shown.ToString() : string.Empty);
		}
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

			var result = HandleSecretKey(Console.ReadKey(intercept: true), buffer, mask, echo, ct);
			if (result is not null)
			{
				return result;
			}
		}

		return null;
	}

	internal async ValueTask<string?> ReadSecretWithCountdownAsync(
		TimeSpan timeout,
		char? mask,
		TextWriter echo,
		LocalKeySource keys,
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

		// A timeout ends the read loop with null; Esc throws and must propagate, even once the timer has fired.
		return await Task.Run(
				function: () => ReadSecretWithCountdownSync(timeout, mask, echo, keys, timeoutCts.Token, cancellationToken),
				cancellationToken: cancellationToken)
			.ConfigureAwait(false);
	}

	private static string? ReadSecretWithCountdownSync(
		TimeSpan timeout,
		char? mask,
		TextWriter echo,
		LocalKeySource keys,
		CancellationToken timeoutCt,
		CancellationToken externalCt)
	{
#pragma warning disable MA0045 // Intentionally synchronous — called via Task.Run from ReadSecretWithCountdownAsync
		ConsoleInputGate.Gate.Wait(externalCt);
#pragma warning restore MA0045
		try
		{
			return ReadSecretWithCountdownCore(timeout, mask, echo, keys, timeoutCt, externalCt);
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
		LocalKeySource keys,
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
			if (keys.KeyAvailable())
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

				var result = HandleSecretKey(keys.ReadKey(), buffer, mask, echo, externalCt);
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
		ConsoleKeyInfo key,
		System.Text.StringBuilder buffer,
		char? mask,
		TextWriter echo,
		CancellationToken ct)
	{
		var (outcome, text) = ApplySecretKey(key, buffer, mask);
		if (text.Length > 0)
		{
			Echo(echo, text);
		}

		switch (outcome)
		{
			case SecretKeyOutcome.Cancel:
				throw new PromptAbandonedException("Prompt cancelled via Esc.", ct);
			case SecretKeyOutcome.Submit:
				EchoLine(echo);
				return buffer.ToString();
			default:
				return null;
		}
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
			return await ReadWithTimeoutRedirectedAsync(
					timeout.Value, (deadline, _) => ReadLineWithEscAsync(echo, deadline), cancellationToken)
				.ConfigureAwait(false);
		}

		return await ReadLineWithCountdownAsync(timeout.Value, defaultLabel, echo, cancellationToken)
			.ConfigureAwait(false);
	}

	// read receives the deadline, cancelled by the timer or the caller, and the caller's own token.
	private async ValueTask<string?> ReadWithTimeoutRedirectedAsync(
		TimeSpan timeout,
		Func<CancellationToken, CancellationToken, ValueTask<string?>> read,
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
			return await read(timeoutCts.Token, cancellationToken).ConfigureAwait(false);
		}
		// Only a cancellation carrying the deadline's own token is a timeout: the timer's state alone cannot say what
		// cancelled. Esc, a secret input that ended, and a transport cancelling its own write (which is never handed the
		// deadline) therefore fail the prompt even when the timer has fired since. Operations handed the deadline, a key
		// read or an echo flush, report a cancellation while it is cancelled as its own (PromptCancellation).
		catch (OperationCanceledException ex) when (ex is not PromptAbandonedException
			&& ex.CancellationToken == timeoutCts.Token
			&& timeoutCts.IsCancellationRequested
			&& !cancellationToken.IsCancellationRequested)
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
			throw new PromptAbandonedException("Prompt cancelled via Esc.", cancellationToken);
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
		Echo(echo, EraseSequence(length));
	}

	private static string EraseSequence(int length) =>
		new string('\b', length) + new string(' ', length) + new string('\b', length);

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
			throw new PromptAbandonedException("Prompt cancelled via Esc.", ct);
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
