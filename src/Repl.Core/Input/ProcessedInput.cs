namespace Repl;

internal sealed record ProcessedInput(
	ArgumentPreprocessingContext Context, InputValidationResult Validation,
	bool IsHandled = false, object? Result = null)
{
	internal ReplInvocationRequest? Request { get; init; }
	internal ReplInvocationRequest? OriginalRequest { get; init; }
	internal ReplInvocationChannel? Channel { get; init; }
	internal bool InheritSessionGlobals { get; init; } = true;
	internal bool WasRewritten => !Context.OriginalArguments.SequenceEqual(Context.Arguments, StringComparer.Ordinal);
}
