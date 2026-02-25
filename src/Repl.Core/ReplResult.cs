namespace Repl;

/// <summary>
/// Default immutable result implementation.
/// </summary>
/// <param name="Kind">Result kind.</param>
/// <param name="Code">Optional result code.</param>
/// <param name="Message">Result message.</param>
/// <param name="Details">Optional details payload.</param>
public sealed record ReplResult(
	string Kind,
	string? Code,
	string Message,
	object? Details) : IReplResult;