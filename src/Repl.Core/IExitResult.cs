namespace Repl;

/// <summary>
/// Represents an explicit process exit code with an optional payload to render.
/// </summary>
public interface IExitResult
{
	/// <summary>
	/// Gets the process exit code to return.
	/// </summary>
	int ExitCode { get; }

	/// <summary>
	/// Gets an optional payload to render using the configured output transformer.
	/// </summary>
	object? Payload { get; }
}
