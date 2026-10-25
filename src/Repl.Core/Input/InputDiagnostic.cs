namespace Repl;

/// <summary>A validation message associated with the original input when its range is known.</summary>
/// <param name="State">Validation state reported by this diagnostic.</param>
/// <param name="Message">Message suitable for a live hint or an execution diagnostic.</param>
/// <param name="Range">Optional range in the original text; null denotes the whole invocation.</param>
public sealed record InputDiagnostic(InputValidationState State, string Message, InputSourceRange? Range = null);
