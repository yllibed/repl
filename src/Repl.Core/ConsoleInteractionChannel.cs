namespace Repl;

internal sealed class ConsoleInteractionChannel(
	InteractionOptions options,
	OutputOptions? outputOptions = null,
	IReplInteractionPresenter? presenter = null,
	TimeProvider? timeProvider = null) : IReplInteractionChannel, ICommandTokenReceiver
{
	private readonly InteractionOptions _options = options;
	private readonly IReplInteractionPresenter _presenter = presenter ?? new ConsoleReplInteractionPresenter(options, outputOptions);
	private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
	private CancellationToken _commandToken;

	/// <summary>
	/// Sets the ambient per-command token. Called by the framework before each command dispatch.
	/// </summary>
	void ICommandTokenReceiver.SetCommandToken(CancellationToken ct) => _commandToken = ct;

	public async ValueTask WriteProgressAsync(
		string label,
		double? percent,
		CancellationToken cancellationToken)
	{
		label = string.IsNullOrWhiteSpace(label)
			? throw new ArgumentException("Label cannot be empty.", nameof(label))
			: label;
		cancellationToken.ThrowIfCancellationRequested();
		await _presenter.PresentAsync(
				new ReplProgressEvent(label, Percent: percent),
				cancellationToken)
			.ConfigureAwait(false);
	}

	public async ValueTask WriteStatusAsync(string text, CancellationToken cancellationToken)
	{
		text = string.IsNullOrWhiteSpace(text)
			? throw new ArgumentException("Status text cannot be empty.", nameof(text))
			: text;
		cancellationToken.ThrowIfCancellationRequested();
		await _presenter.PresentAsync(new ReplStatusEvent(text), cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<int> AskChoiceAsync(
		string name,
		string prompt,
		IReadOnlyList<string> choices,
		int? defaultIndex = null,
		AskOptions? options = null)
	{
		_ = ValidateName(name);
		prompt = string.IsNullOrWhiteSpace(prompt)
			? throw new ArgumentException("Prompt cannot be empty.", nameof(prompt))
			: prompt;
		choices = choices is null || choices.Count == 0
			? throw new ArgumentException("At least one choice is required.", nameof(choices))
			: choices;
		if (defaultIndex is not null && (defaultIndex.Value < 0 || defaultIndex.Value >= choices.Count))
		{
			throw new ArgumentOutOfRangeException(nameof(defaultIndex));
		}

		var effectiveCt = ResolveToken(options);
		effectiveCt.ThrowIfCancellationRequested();

		var effectiveDefaultIndex = defaultIndex ?? 0;

		if (_options.TryGetPrefilledAnswer(name, out var prefilledChoice) && !string.IsNullOrWhiteSpace(prefilledChoice))
		{
			var matchIndex = MatchChoice(choices, prefilledChoice);
			if (matchIndex >= 0)
			{
				return matchIndex;
			}
		}

		var choiceDisplay = string.Join('/', choices.Select((c, i) =>
			i == effectiveDefaultIndex ? c.ToUpperInvariant() : c.ToLowerInvariant()));
		while (true)
		{
			var line = await ReadPromptLineAsync(
					name,
					$"{prompt} [{choiceDisplay}]",
					kind: "choice",
					effectiveCt,
					options?.Timeout,
					defaultLabel: choices[effectiveDefaultIndex])
				.ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(line))
			{
				return HandleMissingAnswer(fallbackValue: effectiveDefaultIndex, "choice");
			}

			var selectedIndex = MatchChoice(choices, line);
			if (selectedIndex >= 0)
			{
				return selectedIndex;
			}

			await _presenter.PresentAsync(
					new ReplStatusEvent($"Invalid choice '{line}'. Please enter one of: {string.Join(", ", choices)}."),
					effectiveCt)
				.ConfigureAwait(false);
		}
	}

	private static int MatchChoice(IReadOnlyList<string> choices, string input) =>
		MatchChoiceByName(input, choices);

	/// <summary>
	/// Matches a text input against a choice list: exact match first, then unambiguous prefix.
	/// Returns the zero-based index of the matched choice, or <c>-1</c> if no match found.
	/// </summary>
	private static int MatchChoiceByName(string input, IReadOnlyList<string> choices)
	{
		// Exact match first.
		for (var i = 0; i < choices.Count; i++)
		{
			if (string.Equals(choices[i], input, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}

		// Unambiguous prefix match.
		var prefixMatch = -1;
		for (var i = 0; i < choices.Count; i++)
		{
			if (choices[i].StartsWith(input, StringComparison.OrdinalIgnoreCase))
			{
				if (prefixMatch >= 0)
				{
					return -1; // Ambiguous prefix.
				}

				prefixMatch = i;
			}
		}

		return prefixMatch;
	}

	public async ValueTask<bool> AskConfirmationAsync(
		string name,
		string prompt,
		bool defaultValue = false,
		AskOptions? options = null)
	{
		_ = ValidateName(name);
		prompt = string.IsNullOrWhiteSpace(prompt)
			? throw new ArgumentException("Prompt cannot be empty.", nameof(prompt))
			: prompt;
		var effectiveCt = ResolveToken(options);
		effectiveCt.ThrowIfCancellationRequested();

		if (_options.TryGetPrefilledAnswer(name, out var prefilledAnswer)
			&& TryParseBoolean(prefilledAnswer, out var resolvedPrefilled))
		{
			return resolvedPrefilled;
		}

		var line = await ReadPromptLineAsync(
				name,
				$"{prompt} [{(defaultValue ? "Y/n" : "y/N")}]",
				kind: "confirmation",
				effectiveCt,
				options?.Timeout,
				defaultLabel: defaultValue ? "yes" : "no")
			.ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(line))
		{
			return HandleMissingAnswer(defaultValue, "confirmation");
		}

		if (string.Equals(line, "y", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(line, "yes", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (string.Equals(line, "n", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(line, "no", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return HandleMissingAnswer(defaultValue, "confirmation");
	}

	public async ValueTask<string> AskTextAsync(
		string name,
		string prompt,
		string? defaultValue = null,
		AskOptions? options = null)
	{
		_ = ValidateName(name);
		prompt = string.IsNullOrWhiteSpace(prompt)
			? throw new ArgumentException("Prompt cannot be empty.", nameof(prompt))
			: prompt;
		var effectiveCt = ResolveToken(options);
		effectiveCt.ThrowIfCancellationRequested();

		if (_options.TryGetPrefilledAnswer(name, out var prefilledText))
		{
			return prefilledText ?? string.Empty;
		}

		var decoratedPrompt = string.IsNullOrWhiteSpace(defaultValue)
			? prompt
			: $"{prompt} [{defaultValue}]";
		var line = await ReadPromptLineAsync(
				name, decoratedPrompt, kind: "text", effectiveCt,
				options?.Timeout, defaultLabel: defaultValue)
			.ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(line))
		{
			return HandleMissingAnswer(defaultValue ?? string.Empty, "text");
		}

		return line;
	}

	public async ValueTask<string> AskSecretAsync(
		string name,
		string prompt,
		AskSecretOptions? options = null)
	{
		_ = ValidateName(name);
		prompt = string.IsNullOrWhiteSpace(prompt)
			? throw new ArgumentException("Prompt cannot be empty.", nameof(prompt))
			: prompt;
		var effectiveCt = ResolveToken(options?.CancellationToken);
		effectiveCt.ThrowIfCancellationRequested();

		if (_options.TryGetPrefilledAnswer(name, out var prefilledSecret))
		{
			return prefilledSecret ?? string.Empty;
		}

		var allowEmpty = options?.AllowEmpty ?? false;
		while (true)
		{
			await _presenter.PresentAsync(
					new ReplPromptEvent(name, prompt, "secret"),
					effectiveCt)
				.ConfigureAwait(false);

			string? line;
			if (options?.Timeout is not null && options.Timeout.Value > TimeSpan.Zero)
			{
				if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
				{
					line = await ReadWithTimeoutRedirectedAsync(effectiveCt, options.Timeout.Value)
						.ConfigureAwait(false);
				}
				else
				{
					line = await ReadSecretWithCountdownAsync(
							options.Timeout.Value, options?.Mask, effectiveCt)
						.ConfigureAwait(false);
				}
			}
			else
			{
				line = await ReadSecretLineAsync(options?.Mask, effectiveCt).ConfigureAwait(false);
			}

			if (string.IsNullOrEmpty(line))
			{
				if (allowEmpty)
				{
					return HandleMissingAnswer(string.Empty, "secret");
				}

				await _presenter.PresentAsync(
						new ReplStatusEvent("A value is required."),
						effectiveCt)
					.ConfigureAwait(false);
				continue;
			}

			return line;
		}
	}

	public async ValueTask<IReadOnlyList<int>> AskMultiChoiceAsync(
		string name,
		string prompt,
		IReadOnlyList<string> choices,
		IReadOnlyList<int>? defaultIndices = null,
		AskMultiChoiceOptions? options = null)
	{
		ValidateMultiChoiceArgs(name, ref prompt, ref choices, defaultIndices);

		var effectiveCt = ResolveToken(options?.CancellationToken);
		effectiveCt.ThrowIfCancellationRequested();

		var effectiveDefaults = defaultIndices ?? [];
		var minSelections = options?.MinSelections ?? 0;
		var maxSelections = options?.MaxSelections;

		if (_options.TryGetPrefilledAnswer(name, out var prefilledMulti) && !string.IsNullOrWhiteSpace(prefilledMulti))
		{
			var parsed = ParseMultiChoiceInput(prefilledMulti, choices);
			if (parsed is not null && IsValidSelection(parsed, minSelections, maxSelections))
			{
				return parsed;
			}
		}

		var choiceDisplay = FormatMultiChoiceDisplay(choices, effectiveDefaults);
		var defaultLabel = FormatMultiChoiceDefaultLabel(effectiveDefaults);

		return await ReadMultiChoiceLoopAsync(
			name, prompt, choices, effectiveDefaults, choiceDisplay, defaultLabel,
			minSelections, maxSelections, effectiveCt, options?.Timeout).ConfigureAwait(false);
	}

	private static void ValidateMultiChoiceArgs(
		string name, ref string prompt, ref IReadOnlyList<string> choices, IReadOnlyList<int>? defaultIndices)
	{
		_ = ValidateName(name);
		prompt = string.IsNullOrWhiteSpace(prompt)
			? throw new ArgumentException("Prompt cannot be empty.", nameof(prompt))
			: prompt;
		choices = choices is null || choices.Count == 0
			? throw new ArgumentException("At least one choice is required.", nameof(choices))
			: choices;
		if (defaultIndices is null)
		{
			return;
		}

		foreach (var idx in defaultIndices)
		{
			if (idx < 0 || idx >= choices.Count)
			{
				throw new ArgumentOutOfRangeException(nameof(defaultIndices));
			}
		}
	}

	private static string FormatMultiChoiceDisplay(IReadOnlyList<string> choices, IReadOnlyList<int> defaults)
	{
		var defaultSet = new HashSet<int>(defaults);
		return string.Join("  ", choices.Select((c, i) =>
			defaultSet.Contains(i) ? $"[{i + 1}*] {c}" : $"[{i + 1}] {c}"));
	}

	private static string? FormatMultiChoiceDefaultLabel(IReadOnlyList<int> defaults) =>
		defaults.Count > 0
			? string.Join(',', defaults.Select(i => (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))
			: null;

	private async ValueTask<IReadOnlyList<int>> ReadMultiChoiceLoopAsync(
		string name, string prompt, IReadOnlyList<string> choices,
		IReadOnlyList<int> effectiveDefaults, string choiceDisplay, string? defaultLabel,
		int minSelections, int? maxSelections, CancellationToken ct, TimeSpan? timeout)
	{
		while (true)
		{
			var line = await ReadPromptLineAsync(
					name, $"{prompt}\n  {choiceDisplay}\n  Enter numbers (comma-separated)",
					kind: "multi-choice", ct, timeout, defaultLabel: defaultLabel)
				.ConfigureAwait(false);

			if (string.IsNullOrWhiteSpace(line))
			{
				return HandleMissingAnswer(effectiveDefaults, "multi-choice");
			}

			var selected = ParseMultiChoiceInput(line, choices);
			if (selected is null)
			{
				await _presenter.PresentAsync(
						new ReplStatusEvent($"Invalid input '{line}'. Enter numbers 1-{choices.Count} separated by commas."),
						ct)
					.ConfigureAwait(false);
				continue;
			}

			if (!IsValidSelection(selected, minSelections, maxSelections))
			{
				var msg = maxSelections is not null
					? $"Please select between {minSelections} and {maxSelections.Value} option(s)."
					: $"Please select at least {minSelections} option(s).";
				await _presenter.PresentAsync(new ReplStatusEvent(msg), ct).ConfigureAwait(false);
				continue;
			}

			return selected;
		}
	}

	private static int[]? ParseMultiChoiceInput(string input, IReadOnlyList<string> choices)
	{
		var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length == 0)
		{
			return null;
		}

		var result = new List<int>(parts.Length);
		foreach (var part in parts)
		{
			// Try as 1-based number first.
			if (int.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var num) && num >= 1 && num <= choices.Count)
			{
				result.Add(num - 1);
				continue;
			}

			// Try as choice name (exact or prefix match).
			var matchIndex = MatchChoiceByName(part, choices);
			if (matchIndex < 0)
			{
				return null;
			}

			result.Add(matchIndex);
		}

		return result.Distinct().Order().ToArray();
	}

	private static bool IsValidSelection(int[] selected, int min, int? max) =>
		selected.Length >= min && (max is null || selected.Length <= max.Value);

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

	/// <summary>
	/// Handles a single keypress during a secret prompt with countdown.
	/// Returns the completed input string on Enter, or <c>null</c> if more input is needed.
	/// </summary>
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

	private CancellationToken ResolveToken(CancellationToken? explicitToken)
	{
		var ct = explicitToken ?? default;
		return ct != default ? ct : _commandToken;
	}

	private static string ValidateName(string name) =>
		string.IsNullOrWhiteSpace(name)
			? throw new ArgumentException("Prompt name cannot be empty.", nameof(name))
			: name;

	private CancellationToken ResolveToken(AskOptions? options) =>
		ResolveToken(options?.CancellationToken);

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

		// Timeout path — redirected input: simple read with timer-based timeout.
		if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
		{
			return await ReadWithTimeoutRedirectedAsync(cancellationToken, timeout.Value)
				.ConfigureAwait(false);
		}

		// Timeout path — interactive: combined countdown display + key reading
		// in a single sequential loop so they never interfere.
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
	/// This avoids the concurrent-write corruption that occurs when countdown
	/// and input run on separate tasks sharing the same console line.
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

		// Show initial countdown suffix.
		Console.Write(lastSuffix);

		while (!externalCt.IsCancellationRequested && (!timeoutCt.IsCancellationRequested || userTyping))
		{
			if (Console.KeyAvailable)
			{
				// First keypress erases the countdown suffix and disarms the timeout.
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

			// Update countdown display (only when user hasn't started typing).
			if (!userTyping && remaining > 0)
			{
				(remaining, lastSuffix, lastTickMs) = TickCountdown(
					remaining, defaultLabel, lastSuffix, lastTickMs);
			}
		}

		// Timeout or cancellation — clean up and signal default.
		if (lastSuffix.Length > 0)
		{
			EraseInline(lastSuffix.Length);
		}

		Console.WriteLine();
		return new ConsoleLineReader.ReadResult(Line: null, Escaped: false);
	}

	/// <summary>
	/// Handles a single keypress during the countdown prompt.
	/// Returns a <see cref="ConsoleLineReader.ReadResult"/> if the line is complete
	/// (Enter or Esc), or <c>null</c> if more input is needed.
	/// </summary>
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
	/// Extracted for testability — every character must occupy exactly one console cell
	/// so that <c>\b</c>-based erasure works correctly.
	/// </summary>
	internal static string FormatCountdownSuffix(int remainingSeconds, string? defaultLabel) =>
		string.IsNullOrWhiteSpace(defaultLabel)
			? $" ({remainingSeconds}s)"
			: $" ({remainingSeconds}s -> {defaultLabel})";

	private T HandleMissingAnswer<T>(T fallbackValue, string promptKind)
	{
		if (_options.PromptFallback == PromptFallback.Fail)
		{
			throw new InvalidOperationException($"No {promptKind} answer was provided in non-interactive mode.");
		}

		return fallbackValue;
	}

	private static bool TryParseBoolean(string? input, out bool value)
	{
		switch (input?.Trim().ToLowerInvariant())
		{
			case "y":
			case "yes":
			case "true":
			case "1":
				value = true;
				return true;
			case "n":
			case "no":
			case "false":
			case "0":
				value = false;
				return true;
			default:
				value = false;
				return false;
		}
	}
}
