namespace Repl;

/// <summary>An explicit adapter decision: continue, replace arguments, or handle the invocation.</summary>
public sealed class ArgumentPreprocessingResult
{
	private ArgumentPreprocessingResult(
		IReadOnlyList<string>? arguments, bool handled, object? result,
		IReadOnlyList<InputSourceRange?>? origins, InputValidationResult? validation)
	{
		Arguments = arguments is null ? null : Array.AsReadOnly(arguments.ToArray());
		Origins = origins is null ? null : Array.AsReadOnly(origins.ToArray());
		IsHandled = handled;
		Result = result;
		Validation = validation ?? InputValidationResult.Valid();
	}

	internal IReadOnlyList<string>? Arguments { get; }
	internal IReadOnlyList<InputSourceRange?>? Origins { get; }
	internal bool IsHandled { get; }
	internal object? Result { get; }
	internal InputValidationResult Validation { get; }

	/// <summary>Passes the effective arguments to the next adapter.</summary>
	/// <param name="validation">Optional diagnostics from this adapter.</param>
	public static ArgumentPreprocessingResult Continue(InputValidationResult? validation = null) =>
		new(arguments: null, handled: false, result: null, origins: null, validation);

	/// <summary>Replaces arguments without restarting the adapter chain.</summary>
	/// <param name="arguments">Arguments to pass to the next stage.</param>
	/// <param name="origins">Optional original-text range for each replacement argument.</param>
	/// <param name="validation">Optional diagnostics from this adapter.</param>
	public static ArgumentPreprocessingResult Replace(
		IReadOnlyList<string> arguments, IReadOnlyList<InputSourceRange?>? origins = null,
		InputValidationResult? validation = null)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		if (arguments.Any(static argument => argument is null))
		{
			throw new ArgumentException("Arguments cannot contain null values.", nameof(arguments));
		}
		if (origins is not null && origins.Count != arguments.Count)
		{
			throw new ArgumentException("Each replacement argument must have one origin entry.", nameof(origins));
		}
		if (origins is not null && origins.Any(static origin => origin is { Start: < 0 } or { Length: < 0 }))
		{
			throw new ArgumentException("Origin ranges cannot have a negative start or length.", nameof(origins));
		}
		return new(arguments, handled: false, result: null, origins, validation);
	}

	/// <summary>Stops adapters and normal dispatch. Preview modes never render or exit on this result.</summary>
	/// <param name="result">A normal Repl result; use Results.Exit for an explicit exit.</param>
	/// <param name="validation">Optional diagnostics from this adapter.</param>
	public static ArgumentPreprocessingResult Handled(object? result = null, InputValidationResult? validation = null) =>
		new(arguments: null, handled: true, result, origins: null, validation);
}
