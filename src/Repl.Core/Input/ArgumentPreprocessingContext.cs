namespace Repl;

/// <summary>Read-only input for an argument adapter. Callbacks must filter external actions by mode.</summary>
public sealed class ArgumentPreprocessingContext
{
	internal ArgumentPreprocessingContext(
		IReadOnlyList<string> originalArguments, IReadOnlyList<string> arguments,
		IReadOnlyList<string> scopeTokens, InputProcessingMode mode, InputPurpose purpose,
		string? text, int? cursor, IReadOnlyList<InputSourceRange?> origins, bool isInteractive = false)
	{
		OriginalArguments = Array.AsReadOnly(originalArguments.ToArray());
		Arguments = Array.AsReadOnly(arguments.ToArray());
		ScopeTokens = Array.AsReadOnly(scopeTokens.ToArray());
		Mode = mode;
		Purpose = purpose;
		Text = text;
		Cursor = cursor;
		ArgumentOrigins = Array.AsReadOnly(origins.ToArray());
		IsInteractive = isInteractive;
	}

	internal bool IsInteractive { get; }

	/// <summary>Gets arguments before any adapter ran.</summary>
	public IReadOnlyList<string> OriginalArguments { get; }
	/// <summary>Gets arguments produced by the preceding adapter.</summary>
	public IReadOnlyList<string> Arguments { get; }
	/// <summary>Gets the interactive scope, which is separate from the editable arguments.</summary>
	public IReadOnlyList<string> ScopeTokens { get; }
	/// <summary>Gets the processing mode; only Execution denotes submission.</summary>
	public InputProcessingMode Mode { get; }
	/// <summary>Gets the intent recognizable from the current arguments.</summary>
	public InputPurpose Purpose { get; }
	/// <summary>Gets original editor text when available.</summary>
	public string? Text { get; }
	/// <summary>Gets the cursor position in the original text when available.</summary>
	public int? Cursor { get; }
	/// <summary>Gets each effective argument's range in the original text, or null for an unmapped argument.</summary>
	public IReadOnlyList<InputSourceRange?> ArgumentOrigins { get; }
}
