namespace Repl.Spectre;

/// <summary>
/// Handles interaction requests using Spectre.Console rich prompts.
/// Returns <see cref="InteractionResult.Unhandled"/> when the terminal
/// does not support interactive Spectre prompts (hosted sessions, redirected input).
/// </summary>
public sealed class SpectreInteractionHandler : IReplInteractionHandler
{
	/// <inheritdoc />
	public ValueTask<InteractionResult> TryHandleAsync(
		InteractionRequest request, CancellationToken cancellationToken)
	{
		if (!CanHandle())
		{
			return new ValueTask<InteractionResult>(InteractionResult.Unhandled);
		}

		return request switch
		{
			AskChoiceRequest r => HandleChoiceAsync(r, cancellationToken),
			AskMultiChoiceRequest r => HandleMultiChoiceAsync(r, cancellationToken),
			AskConfirmationRequest r => HandleConfirmationAsync(r, cancellationToken),
			AskTextRequest r => HandleTextAsync(r, cancellationToken),
			AskSecretRequest r => HandleSecretAsync(r, cancellationToken),
			ClearScreenRequest => HandleClearScreenAsync(),
			_ => new ValueTask<InteractionResult>(InteractionResult.Unhandled),
		};
	}

	/// <summary>
	/// Returns <c>true</c> when Spectre prompts can operate:
	/// non-hosted session and non-redirected input.
	/// </summary>
	private static bool CanHandle()
	{
		if (ReplSessionIO.IsHostedSession)
		{
			return false;
		}

		if (Console.IsInputRedirected)
		{
			return false;
		}

		return true;
	}

	private static async ValueTask<InteractionResult> HandleChoiceAsync(
		AskChoiceRequest r, CancellationToken ct)
	{
		var console = SessionAnsiConsole.Create();
		var choices = StripMnemonics(r.Choices);

		// Reorder so the default item appears first (Spectre highlights first item).
		if (r.DefaultIndex is { } idx && idx > 0 && idx < choices.Count)
		{
			(choices[0], choices[idx]) = (choices[idx], choices[0]);
		}

		var prompt = new SelectionPrompt<string>()
			.Title(r.Prompt)
			.AddChoices(choices);

		if (r.DefaultIndex is >= 0)
		{
			prompt.HighlightStyle(new Style(Color.Blue));
		}

		// Spectre.Console's Prompt() is inherently synchronous (console I/O); Task.Run is the intended pattern.
#pragma warning disable MA0045
		var selected = await Task.Run(() => console.Prompt(prompt), ct).ConfigureAwait(false);
#pragma warning restore MA0045

		// Map back to original index (account for potential reorder).
		var selectedIndex = MapBackToOriginalIndex(selected, r.Choices);
		return InteractionResult.Success(selectedIndex);
	}

	private static async ValueTask<InteractionResult> HandleMultiChoiceAsync(
		AskMultiChoiceRequest r, CancellationToken ct)
	{
		var console = SessionAnsiConsole.Create();
		var choices = StripMnemonics(r.Choices);
		var prompt = new MultiSelectionPrompt<string>()
			.Title(r.Prompt)
			.AddChoices(choices);

		if (r.DefaultIndices is { } defaults)
		{
			foreach (var defaultIdx in defaults.Where(i => i >= 0 && i < choices.Count))
			{
				prompt.Select(choices[defaultIdx]);
			}
		}

		if (r.Options?.MinSelections is > 0)
		{
			prompt.Required();
		}

#pragma warning disable MA0045
		var selected = await Task.Run(() => console.Prompt(prompt), ct).ConfigureAwait(false);
#pragma warning restore MA0045

		var indices = selected
			.Select(s => MapBackToOriginalIndex(s, r.Choices))
			.Where(i => i >= 0)
			.Order()
			.ToArray();

		return InteractionResult.Success((IReadOnlyList<int>)indices);
	}

	private static async ValueTask<InteractionResult> HandleConfirmationAsync(
		AskConfirmationRequest r, CancellationToken ct)
	{
		var console = SessionAnsiConsole.Create();
		var prompt = new ConfirmationPrompt(r.Prompt)
		{
			DefaultValue = r.DefaultValue,
		};

#pragma warning disable MA0045
		var result = await Task.Run(() => console.Prompt(prompt), ct).ConfigureAwait(false);
#pragma warning restore MA0045
		return InteractionResult.Success(result);
	}

	private static async ValueTask<InteractionResult> HandleTextAsync(
		AskTextRequest r, CancellationToken ct)
	{
		var console = SessionAnsiConsole.Create();
		var prompt = new TextPrompt<string>(r.Prompt)
			.AllowEmpty();

		if (!string.IsNullOrEmpty(r.DefaultValue))
		{
			prompt.DefaultValue(r.DefaultValue);
		}

#pragma warning disable MA0045
		var result = await Task.Run(() => console.Prompt(prompt), ct).ConfigureAwait(false);
#pragma warning restore MA0045
		return InteractionResult.Success(result);
	}

	private static async ValueTask<InteractionResult> HandleSecretAsync(
		AskSecretRequest r, CancellationToken ct)
	{
		var console = SessionAnsiConsole.Create();
		var prompt = new TextPrompt<string>(r.Prompt);

		var effectiveMask = r.Options is { } options ? options.Mask : '*';
		prompt.Secret(effectiveMask);

		if (r.Options?.AllowEmpty == true)
		{
			prompt.AllowEmpty();
		}

#pragma warning disable MA0045
		var result = await Task.Run(() => console.Prompt(prompt), ct).ConfigureAwait(false);
#pragma warning restore MA0045
		return InteractionResult.Success(result);
	}

	private static ValueTask<InteractionResult> HandleClearScreenAsync()
	{
		var console = SessionAnsiConsole.Create();
		console.Clear();
		return new ValueTask<InteractionResult>(InteractionResult.Success(value: true));
	}

	/// <summary>
	/// Strips mnemonic markers from choice labels for Spectre display.
	/// </summary>
	internal static List<string> StripMnemonics(IReadOnlyList<string> choices)
	{
		var result = new List<string>(choices.Count);
		for (var i = 0; i < choices.Count; i++)
		{
			var (display, _) = MnemonicParser.Parse(choices[i]);
			result.Add(display);
		}

		return result;
	}

	/// <summary>
	/// Maps a selected display string back to the original choice index
	/// by stripping mnemonics from original choices and matching.
	/// Returns <c>-1</c> when the selected value does not match any choice.
	/// </summary>
	internal static int MapBackToOriginalIndex(string selected, IReadOnlyList<string> originalChoices)
	{
		for (var i = 0; i < originalChoices.Count; i++)
		{
			var (display, _) = MnemonicParser.Parse(originalChoices[i]);
			if (string.Equals(display, selected, StringComparison.Ordinal))
			{
				return i;
			}
		}

		return -1;
	}
}
