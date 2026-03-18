namespace Repl;

/// <summary>
/// Controls how runtime interaction prompts are handled in MCP mode.
/// </summary>
public enum InteractivityMode
{
	/// <summary>Use prefilled values, fail with descriptive error if missing.</summary>
	PrefillThenFail,

	/// <summary>Use prefilled values, fall back to defaults if missing.</summary>
	PrefillThenDefaults,

	/// <summary>Use prefilled values, then elicitation, then sampling, then fail.</summary>
	PrefillThenElicitation,

	/// <summary>Use prefilled values, then sampling (skip elicitation), then fail.</summary>
	PrefillThenSampling,
}
