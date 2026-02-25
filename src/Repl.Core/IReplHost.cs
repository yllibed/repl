namespace Repl;

/// <summary>
/// Represents an input/output host used to run interactive REPL sessions outside the local console.
/// </summary>
public interface IReplHost
{
	/// <summary>
	/// Gets the host input reader.
	/// </summary>
	TextReader Input { get; }

	/// <summary>
	/// Gets the host output writer.
	/// </summary>
	TextWriter Output { get; }
}
