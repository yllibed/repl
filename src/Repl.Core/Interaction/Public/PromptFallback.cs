namespace Repl.Interaction;

/// <summary>
/// Defines fallback policy for unanswered prompts.
/// </summary>
public enum PromptFallback
{
	/// <summary>
	/// Uses configured default value.
	/// </summary>
	UseDefault,

	/// <summary>
	/// Fails when no answer is available.
	/// </summary>
	Fail,
}
