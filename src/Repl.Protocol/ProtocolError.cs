namespace Repl.Protocol;

/// <summary>
/// Machine-readable error descriptor.
/// </summary>
/// <param name="Code">Error code.</param>
/// <param name="Message">Error message.</param>
public sealed record ProtocolError(
	string Code,
	string Message);