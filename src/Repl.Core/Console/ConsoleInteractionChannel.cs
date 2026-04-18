namespace Repl;

internal sealed partial class ConsoleInteractionChannel(
	InteractionOptions options,
	OutputOptions? outputOptions = null,
	IReplInteractionPresenter? presenter = null,
	IReadOnlyList<IReplInteractionHandler>? handlers = null,
	TimeProvider? timeProvider = null) : IReplInteractionChannel, ICommandTokenReceiver
{
	private readonly InteractionOptions _options = options;
	private readonly IReplInteractionPresenter _presenter = presenter ?? new ConsoleReplInteractionPresenter(options, outputOptions);
	private readonly IReadOnlyList<IReplInteractionHandler> _handlers = handlers ?? [];
	private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
	private CancellationToken _commandToken;

	/// <summary>
	/// Dispatches a request through the handler pipeline.
	/// Returns the <see cref="InteractionResult"/> from the first handler that handles it,
	/// or <see cref="InteractionResult.Unhandled"/> if none did.
	/// </summary>
	private async ValueTask<InteractionResult> TryDispatchAsync(
		InteractionRequest request, CancellationToken ct)
	{
		foreach (var handler in _handlers)
		{
			var result = await handler.TryHandleAsync(request, ct).ConfigureAwait(false);
			if (result.Handled)
			{
				return result;
			}
		}

		return InteractionResult.Unhandled;
	}

	/// <inheritdoc />
	public async ValueTask<TResult> DispatchAsync<TResult>(
		InteractionRequest<TResult> request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		cancellationToken.ThrowIfCancellationRequested();

		var dispatched = await TryDispatchAsync(request, cancellationToken).ConfigureAwait(false);
		if (dispatched.Handled)
		{
			return (TResult)dispatched.Value!;
		}

		if (await TryHandleBuiltInDispatchAsync(request, cancellationToken).ConfigureAwait(false) is { Handled: true } builtIn)
		{
			return (TResult)builtIn.Value!;
		}

		throw new NotSupportedException(
			$"No handler registered for interaction request '{request.GetType().Name}'.");
	}

	private async ValueTask<InteractionResult> TryHandleBuiltInDispatchAsync(
		InteractionRequest request,
		CancellationToken cancellationToken)
	{
		switch (request)
		{
			case WriteNoticeRequest notice:
				await PresentFeedbackAsync(
						notice.Text,
						new ReplNoticeEvent(notice.Text),
						cancellationToken)
					.ConfigureAwait(false);
				return InteractionResult.Success(value: true);

			case WriteWarningRequest warning:
				await PresentFeedbackAsync(
						warning.Text,
						new ReplWarningEvent(warning.Text),
						cancellationToken)
					.ConfigureAwait(false);
				return InteractionResult.Success(value: true);

			case WriteProblemRequest problem:
				if (string.IsNullOrWhiteSpace(problem.Summary))
				{
					throw new ArgumentException("Problem summary cannot be empty.", nameof(request));
				}

				await _presenter.PresentAsync(
						new ReplProblemEvent(problem.Summary, problem.Details, problem.Code),
						cancellationToken)
					.ConfigureAwait(false);
				return InteractionResult.Success(value: true);

			case WriteProgressRequest progress:
				await _presenter.PresentAsync(
						new ReplProgressEvent(
							progress.Label,
							Percent: progress.Percent,
							State: progress.State,
							Details: progress.Details),
						cancellationToken)
					.ConfigureAwait(false);
				return InteractionResult.Success(value: true);

			default:
				return InteractionResult.Unhandled;
		}
	}

	private async ValueTask PresentFeedbackAsync(
		string text,
		ReplInteractionEvent interactionEvent,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new ArgumentException("Feedback text cannot be empty.", nameof(text));
		}

		await _presenter.PresentAsync(interactionEvent, cancellationToken).ConfigureAwait(false);
	}

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

		var dispatched = await TryDispatchAsync(
				new WriteProgressRequest(
					Label: label,
					Percent: percent,
					CancellationToken: cancellationToken),
				cancellationToken)
			.ConfigureAwait(false);
		if (dispatched.Handled)
		{
			return;
		}

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

		var dispatched = await TryDispatchAsync(new WriteStatusRequest(text, cancellationToken), cancellationToken)
			.ConfigureAwait(false);
		if (dispatched.Handled)
		{
			return;
		}

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

		var dispatched = await TryDispatchAsync(
			new AskChoiceRequest(name, prompt, choices, defaultIndex, options), effectiveCt).ConfigureAwait(false);
		if (dispatched.Handled)
		{
			return (int)dispatched.Value!;
		}

		return await ReadChoiceTextFallbackAsync(name, prompt, choices, effectiveDefaultIndex, options?.Timeout, effectiveCt)
			.ConfigureAwait(false);
	}

	private async ValueTask<int> ReadChoiceTextFallbackAsync(
		string name, string prompt, IReadOnlyList<string> choices,
		int effectiveDefaultIndex, TimeSpan? timeout, CancellationToken ct)
	{
		var shortcuts = MnemonicParser.AssignShortcuts(choices);
		var parsedChoices = new (string Display, char? Shortcut)[choices.Count];
		for (var i = 0; i < choices.Count; i++)
		{
			parsedChoices[i] = MnemonicParser.Parse(choices[i]);
		}

		var choiceDisplay = FormatChoiceDisplayText(parsedChoices, shortcuts, effectiveDefaultIndex);

		while (true)
		{
			var line = await ReadPromptLineAsync(
					name,
					$"{prompt} [{choiceDisplay}]",
					kind: "choice",
					ct,
					timeout,
					defaultLabel: parsedChoices[effectiveDefaultIndex].Display)
				.ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(line))
			{
				return HandleMissingAnswer(fallbackValue: effectiveDefaultIndex, "choice");
			}

			var selectedIndex = MatchChoiceWithMnemonics(line, choices, parsedChoices, shortcuts);
			if (selectedIndex >= 0)
			{
				return selectedIndex;
			}

			var displayChoices = parsedChoices.Select(p => p.Display).ToArray();
			await _presenter.PresentAsync(
					new ReplStatusEvent($"Invalid choice '{line}'. Please enter one of: {string.Join(", ", displayChoices)}."),
					ct)
				.ConfigureAwait(false);
		}
	}

	private static string FormatChoiceDisplayText(
		(string Display, char? Shortcut)[] parsedChoices, char?[] shortcuts, int defaultIndex)
	{
		return string.Join(" / ", parsedChoices.Select((p, i) =>
		{
			var text = MnemonicParser.FormatText(p.Display, shortcuts[i]);
			return i == defaultIndex ? text.ToUpperInvariant() : text;
		}));
	}

	private static int MatchChoiceWithMnemonics(
		string line, IReadOnlyList<string> choices,
		(string Display, char? Shortcut)[] parsedChoices, char?[] shortcuts)
	{
		// Try matching shortcut key
		if (line.Length == 1)
		{
			for (var i = 0; i < shortcuts.Length; i++)
			{
				if (shortcuts[i] is { } sc
					&& char.ToUpperInvariant(sc) == char.ToUpperInvariant(line[0]))
				{
					return i;
				}
			}
		}

		// Try original label match
		var selectedIndex = MatchChoiceByName(line, choices);
		if (selectedIndex >= 0)
		{
			return selectedIndex;
		}

		// Try display text match (without mnemonic markers)
		var displayChoices = parsedChoices.Select(p => p.Display).ToArray();
		return MatchChoiceByName(line, displayChoices);
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
		bool? defaultValue = null,
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

		var effectiveDefault = defaultValue ?? false;
		var dispatched = await TryDispatchAsync(
			new AskConfirmationRequest(name, prompt, effectiveDefault, options), effectiveCt).ConfigureAwait(false);
		if (dispatched.Handled)
		{
			return (bool)dispatched.Value!;
		}

		var line = await ReadPromptLineAsync(
				name,
				$"{prompt} [{(effectiveDefault ? "Y/n" : "y/N")}]",
				kind: "confirmation",
				effectiveCt,
				options?.Timeout,
				defaultLabel: effectiveDefault ? "yes" : "no")
			.ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(line))
		{
			return HandleMissingAnswer(effectiveDefault, "confirmation");
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

		return HandleMissingAnswer(effectiveDefault, "confirmation");
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

		var dispatched = await TryDispatchAsync(
			new AskTextRequest(name, prompt, defaultValue, options), effectiveCt).ConfigureAwait(false);
		if (dispatched.Handled)
		{
			return (string)dispatched.Value!;
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

		var dispatched = await TryDispatchAsync(
			new AskSecretRequest(name, prompt, options), effectiveCt).ConfigureAwait(false);
		if (dispatched.Handled)
		{
			return (string)dispatched.Value!;
		}

		return await ReadSecretLoopAsync(name, prompt, options, effectiveCt).ConfigureAwait(false);
	}

	private async ValueTask<string> ReadSecretLoopAsync(
		string name, string prompt, AskSecretOptions? options, CancellationToken ct)
	{
		var allowEmpty = options?.AllowEmpty ?? false;
		while (true)
		{
			await _presenter.PresentAsync(
					new ReplPromptEvent(name, prompt, "secret"),
					ct)
				.ConfigureAwait(false);

			string? line;
			if (options?.Timeout is not null && options.Timeout.Value > TimeSpan.Zero)
			{
				if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
				{
					line = await ReadWithTimeoutRedirectedAsync(options.Timeout.Value, ct)
						.ConfigureAwait(false);
				}
				else
				{
					line = await ReadSecretWithCountdownAsync(
							options.Timeout.Value, options.Mask, ct)
						.ConfigureAwait(false);
				}
			}
			else
			{
				line = await ReadSecretLineAsync(options is not null ? options.Mask : '*', ct).ConfigureAwait(false);
			}

			if (string.IsNullOrEmpty(line))
			{
				if (allowEmpty)
				{
					return HandleMissingAnswer(string.Empty, "secret");
				}

				await _presenter.PresentAsync(
						new ReplStatusEvent("A value is required."),
						ct)
					.ConfigureAwait(false);
				continue;
			}

			return line;
		}
	}

	public async ValueTask ClearScreenAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var dispatched = await TryDispatchAsync(new ClearScreenRequest(), cancellationToken)
			.ConfigureAwait(false);
		if (dispatched.Handled)
		{
			return;
		}

		await _presenter.PresentAsync(new ReplClearScreenEvent(), cancellationToken)
			.ConfigureAwait(false);
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

		var dispatched = await TryDispatchAsync(
			new AskMultiChoiceRequest(name, prompt, choices, defaultIndices, options), effectiveCt).ConfigureAwait(false);
		if (dispatched.Handled)
		{
			return (IReadOnlyList<int>)dispatched.Value!;
		}

		var choiceDisplay = FormatMultiChoiceDisplay(choices, effectiveDefaults);
		var defaultLabel = FormatMultiChoiceDefaultLabel(effectiveDefaults);

		return await ReadMultiChoiceTextFallbackAsync(
			name, prompt, choices, effectiveDefaults, choiceDisplay, defaultLabel,
			minSelections, maxSelections, options?.Timeout, effectiveCt).ConfigureAwait(false);
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

	private async ValueTask<IReadOnlyList<int>> ReadMultiChoiceTextFallbackAsync(
		string name, string prompt, IReadOnlyList<string> choices,
		IReadOnlyList<int> effectiveDefaults, string choiceDisplay, string? defaultLabel,
		int minSelections, int? maxSelections, TimeSpan? timeout, CancellationToken ct)
	{
		while (true)
		{
			var line = await ReadPromptLineAsync(
					name, $"{prompt}\r\n  {choiceDisplay}\r\n  Enter numbers (comma-separated)",
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
