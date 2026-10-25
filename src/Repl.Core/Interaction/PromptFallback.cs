namespace Repl.Interaction;

/// <summary>
/// Defines fallback policy for unanswered prompts.
/// </summary>
/// <remarks>
/// A prompt whose hosted terminal disconnects is cancelled instead, so no answer is chosen for a user who is gone.
/// </remarks>
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
