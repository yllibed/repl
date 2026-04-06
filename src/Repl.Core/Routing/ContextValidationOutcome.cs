namespace Repl;

internal readonly record struct ContextValidationOutcome(bool IsValid, IReplResult? Failure)
{
	public static ContextValidationOutcome Success { get; } =
		new(IsValid: true, Failure: null);

	public static ContextValidationOutcome FromFailure(IReplResult failure) =>
		new(IsValid: false, Failure: failure);
}
