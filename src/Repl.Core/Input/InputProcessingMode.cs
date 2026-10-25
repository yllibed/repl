namespace Repl;

/// <summary>Identifies when an advanced input callback is running.</summary>
public enum InputProcessingMode
{
	/// <summary>An invocation has been submitted.</summary>
	Execution,
	/// <summary>The interactive editor is refreshing its live analysis.</summary>
	Preview,
	/// <summary>The user explicitly requested interactive completion.</summary>
	Completion,
	/// <summary>A shell completion bridge is analyzing its transported command line.</summary>
	ShellCompletion,
}
