using Repl.Interaction;

namespace Repl;

/// <summary>
/// Built-in <see cref="IReplInteractionHandler"/> that renders interactive
/// arrow-key menus for <see cref="AskChoiceRequest"/> and <see cref="AskMultiChoiceRequest"/>
/// when the terminal supports ANSI escape sequences and direct key reading.
/// Returns <see cref="InteractionResult.Unhandled"/> when conditions are not met,
/// allowing the text-based fallback in <see cref="ConsoleInteractionChannel"/> to run.
/// </summary>
internal sealed partial class RichPromptInteractionHandler(
	OutputOptions? outputOptions = null,
	IReplInteractionPresenter? presenter = null) : IReplInteractionHandler
{
	private readonly bool _useRichPrompts = outputOptions?.IsAnsiEnabled() ?? false;
	private readonly AnsiPalette? _palette = outputOptions is not null && outputOptions.IsAnsiEnabled()
		? outputOptions.ResolvePalette() : null;

	/// <inheritdoc />
	public async ValueTask<InteractionResult> TryHandleAsync(
		InteractionRequest request, CancellationToken cancellationToken)
	{
		if (!CanUseRichPrompts())
		{
			return InteractionResult.Unhandled;
		}

		return request switch
		{
			AskChoiceRequest r => await HandleChoiceRequestAsync(r, cancellationToken)
				.ConfigureAwait(false),
			AskMultiChoiceRequest r => await HandleMultiChoiceRequestAsync(r, cancellationToken)
				.ConfigureAwait(false),
			_ => InteractionResult.Unhandled,
		};
	}

	private async ValueTask<InteractionResult> HandleChoiceRequestAsync(
		AskChoiceRequest r, CancellationToken ct)
	{
		await PresentPromptAsync(r.Name, r.Prompt, "choice", ct).ConfigureAwait(false);
		var defaultIndex = r.DefaultIndex ?? 0;
		var richResult = await Task.Run(
			() => ReadChoiceInteractiveSync(r.Prompt, r.Choices, defaultIndex, ct), ct)
			.ConfigureAwait(false);
		return InteractionResult.Success(richResult >= 0 ? richResult : defaultIndex);
	}

	private async ValueTask<InteractionResult> HandleMultiChoiceRequestAsync(
		AskMultiChoiceRequest r, CancellationToken ct)
	{
		await PresentPromptAsync(r.Name, r.Prompt, "multi-choice", ct).ConfigureAwait(false);
		var defaults = r.DefaultIndices ?? [];
		var min = r.Options?.MinSelections ?? 0;
		var max = r.Options?.MaxSelections;
		var richResult = await Task.Run(
			() => ReadMultiChoiceInteractiveSync(r.Prompt, r.Choices, defaults, min, max, ct), ct)
			.ConfigureAwait(false);
		if (richResult is not null)
		{
			return InteractionResult.Success((IReadOnlyList<int>)richResult);
		}

		// Esc pressed → return defaults
		return InteractionResult.Success((IReadOnlyList<int>)defaults);
	}

	private async ValueTask PresentPromptAsync(
		string name, string prompt, string kind, CancellationToken ct)
	{
		if (presenter is not null)
		{
			await presenter.PresentAsync(new ReplPromptEvent(name, prompt, kind), ct)
				.ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Returns <c>true</c> when the terminal supports interactive arrow-key menus.
	/// </summary>
	private bool CanUseRichPrompts()
	{
		if (ReplSessionIO.IsSessionActive)
		{
			return ReplSessionIO.AnsiSupport == true && ReplSessionIO.KeyReader is not null;
		}

		return _useRichPrompts && !Console.IsInputRedirected;
	}
}
