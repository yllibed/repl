namespace Repl;

internal sealed record ProcessedInput(
	ArgumentPreprocessingContext Context, InputValidationResult Validation,
	bool IsHandled = false, object? Result = null)
{
	internal bool WasRewritten => !Context.OriginalArguments.SequenceEqual(Context.Arguments, StringComparer.Ordinal);
}
