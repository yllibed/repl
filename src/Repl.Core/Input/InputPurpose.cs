namespace Repl;

/// <summary>The intent recognized so far, before application-specific syntax is interpreted.</summary>
public enum InputPurpose
{
	/// <summary>The input has not yet identified an intent.</summary>
	Unknown,
	/// <summary>A command invocation.</summary>
	Command,
	/// <summary>An explicit help request.</summary>
	Help,
	/// <summary>A completion request.</summary>
	Completion,
}
