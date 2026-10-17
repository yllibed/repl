namespace Repl;

internal sealed partial class AutocompleteEngine
{
	internal async ValueTask<ConsoleLineReader.AutocompleteResult?> ResolveAutocompleteAsync(
		ConsoleLineReader.AutocompleteRequest request, IReadOnlyList<string> scopeTokens,
		IServiceProvider serviceProvider, CancellationToken cancellationToken)
	{
		if (!app.OptionsSnapshot.Input.IsEnabled)
		{
			return await ResolveCanonicalAutocompleteAsync(request, scopeTokens, serviceProvider, cancellationToken).ConfigureAwait(false);
		}
		try
		{
			var tokens = InputTokenizer.Tokenize(request.Input);
			var processed = await app.PreprocessInputAsync(tokens.Select(static token => token.Value).ToArray(), scopeTokens,
				request.ExplicitCompletion ? InputProcessingMode.Completion : InputProcessingMode.Preview,
				request.Input, request.Cursor, cancellationToken, isInteractive: true).ConfigureAwait(false);
			if (processed.IsHandled)
			{
				return ApplyInputDiagnostics(new(request.Cursor, 0, []), processed.Validation, request.Input.Length);
			}
			var analysis = await app.AnalyzeInputAsync(processed, serviceProvider, cancellationToken).ConfigureAwait(false);
			var globals = GlobalOptionParser.Parse([.. scopeTokens, .. processed.Context.Arguments],
				app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing);
			using var globalScope = app.GlobalOptionsSnapshotInstance.PushInputValues(globals.CustomGlobalNamedOptions);
			var canonical = processed.WasRewritten ? InputTokenizer.Format(processed.Context.Arguments) : request.Input;
			var cursorMapped = TryMapInputCursor(processed, canonical, request.Cursor, out var canonicalCursor);
			if (processed.WasRewritten && request.ExplicitCompletion
				&& app.OptionsSnapshot.Interactive.Autocomplete.NormalizeInputOnTab
				&& analysis.Validation.State == InputValidationState.Valid)
			{
				return new(0, request.Input.Length, [], NormalizedInput: canonical, NormalizedCursor: canonicalCursor);
			}
			if (!cursorMapped)
			{
				return ApplyInputDiagnostics(new(request.Cursor, 0, []), analysis.Validation, request.Input.Length);
			}
			var result = await ResolveCanonicalAutocompleteAsync(request with { Input = canonical, Cursor = canonicalCursor },
				scopeTokens, serviceProvider, cancellationToken).ConfigureAwait(false)
				?? new ConsoleLineReader.AutocompleteResult(request.Cursor, 0, []);
			if (processed.WasRewritten)
			{
				result = ProjectInputAssist(result, processed, canonical, request.Cursor);
			}
			return ApplyInputDiagnostics(result, analysis.Validation, request.Input.Length);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception exception)
		{
			return new(request.Cursor, 0, [], HintLine: $"Input analysis failed: {exception.Message}");
		}
	}

	internal static bool TryMapInputCursor(ProcessedInput input, string canonical, int cursor, out int mappedCursor)
	{
		mappedCursor = Math.Clamp(cursor, 0, canonical.Length);
		if (!input.WasRewritten) { return true; }
		var tokens = InputTokenizer.Tokenize(canonical);
		for (var index = 0; index < input.Context.ArgumentOrigins.Count && index < tokens.Count; index++)
		{
			if (input.Context.ArgumentOrigins[index] is not { } origin || cursor < origin.Start || cursor > origin.Start + origin.Length)
			{
				continue;
			}
			var token = tokens[index];
			var offset = input.Context.Text is { } original
				? InputTokenizer.MapTokenCursor(original.AsSpan(origin.Start, origin.Length),
					canonical.AsSpan(token.Start, token.Length), cursor - origin.Start)
				: Math.Min(cursor - origin.Start, token.Length);
			mappedCursor = token.Start + offset;
			return true;
		}
		mappedCursor = canonical.Length;
		return false;
	}

	private static ConsoleLineReader.AutocompleteResult ProjectInputAssist(
		ConsoleLineReader.AutocompleteResult assist, ProcessedInput processed, string canonical, int cursor)
	{
		var tokens = InputTokenizer.Tokenize(canonical);
		var classifications = new List<ConsoleLineReader.TokenClassification>();
		foreach (var classification in assist.TokenClassifications ?? [])
		{
			var index = tokens.FindIndex(token => classification.Start >= token.Start && classification.Start < token.Start + token.Length);
			if (index >= 0 && processed.Context.ArgumentOrigins.ElementAtOrDefault(index) is { } origin)
			{
				classifications.Add(new(origin.Start, origin.Length, classification.Kind));
			}
		}
		var replacementIndex = tokens.FindIndex(token => assist.ReplaceStart == token.Start && assist.ReplaceLength == token.Length);
		if (replacementIndex >= 0 && processed.Context.ArgumentOrigins.ElementAtOrDefault(replacementIndex) is { } replacement)
		{
			return assist with { ReplaceStart = replacement.Start, ReplaceLength = replacement.Length, TokenClassifications = classifications };
		}
		// A many-to-one rewrite has no safe inverse unless the adapter supplied a source range.
		return assist with { ReplaceStart = cursor, ReplaceLength = 0, Suggestions = [], TokenClassifications = classifications };
	}

	private static ConsoleLineReader.AutocompleteResult ApplyInputDiagnostics(
		ConsoleLineReader.AutocompleteResult assist, InputValidationResult validation, int textLength)
	{
		if (validation.State == InputValidationState.Valid) { return assist; }
		var classifications = (assist.TokenClassifications ?? []).ToList();
		foreach (var diagnostic in validation.Diagnostics)
		{
			if (diagnostic.State != InputValidationState.Invalid || diagnostic.Range is not { } range
				|| range.Start < 0 || range.Length < 0 || range.Start > textLength - range.Length) { continue; }
			classifications.Add(new(range.Start, range.Length, ConsoleLineReader.AutocompleteSuggestionKind.Invalid));
		}
		var message = validation.Diagnostics.Count == 0 ? "More input is required." : validation.Diagnostics[0].Message;
		return assist with { HintLine = $"{validation.State}: {message}", TokenClassifications = classifications };
	}
}
