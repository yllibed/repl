namespace Repl;

/// <summary>
/// Provides access to the ambient REPL logging context for the current execution.
/// </summary>
public interface IReplLogContextAccessor
{
	/// <summary>
	/// Gets the current REPL logging context snapshot.
	/// </summary>
	ReplLogContext Current { get; }
}
