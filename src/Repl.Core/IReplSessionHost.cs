namespace Repl;

/// <summary>
/// Optional host contract exposing a stable session identifier.
/// </summary>
public interface IReplSessionHost : IReplHost
{
	/// <summary>
	/// Gets the unique identifier of the host session.
	/// </summary>
	string SessionId { get; }
}
