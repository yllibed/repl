namespace Repl;

/// <summary>Describes whether input can run, needs more text, or contains an error.</summary>
public enum InputValidationState
{
	/// <summary>The analyzed input satisfies the checks that ran.</summary>
	Valid,
	/// <summary>More input or an execution-time check is required.</summary>
	Incomplete,
	/// <summary>The input fails validation.</summary>
	Invalid,
}
