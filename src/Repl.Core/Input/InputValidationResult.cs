namespace Repl;

/// <summary>Returns the outcome of an input check without executing a command.</summary>
public sealed class InputValidationResult
{
	/// <summary>Creates a result with optional diagnostics.</summary>
	/// <param name="state">Overall validation state.</param>
	/// <param name="diagnostics">Messages from the check.</param>
	public InputValidationResult(InputValidationState state, IReadOnlyList<InputDiagnostic>? diagnostics = null)
	{
		Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
		State = Diagnostics.Select(static diagnostic => diagnostic.State).Append(state).Max();
	}

	/// <summary>Gets the overall state, including any more severe diagnostic.</summary>
	public InputValidationState State { get; }

	/// <summary>Gets the messages returned by the check.</summary>
	public IReadOnlyList<InputDiagnostic> Diagnostics { get; }

	/// <summary>Returns successful validation.</summary>
	public static InputValidationResult Valid() => new(InputValidationState.Valid);

	/// <summary>Reports that more input or another check is needed.</summary>
	/// <param name="message">Explanation of what is missing.</param>
	/// <param name="range">Optional original input range.</param>
	public static InputValidationResult Incomplete(string message, InputSourceRange? range = null) =>
		new(InputValidationState.Incomplete, [new(InputValidationState.Incomplete, message, range)]);

	/// <summary>Rejects input with a diagnostic.</summary>
	/// <param name="message">Reason for rejection.</param>
	/// <param name="range">Optional original input range.</param>
	public static InputValidationResult Invalid(string message, InputSourceRange? range = null) =>
		new(InputValidationState.Invalid, [new(InputValidationState.Invalid, message, range)]);
}
