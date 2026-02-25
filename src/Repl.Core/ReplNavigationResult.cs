namespace Repl;

/// <summary>
/// Navigation instruction returned by a command result helper.
/// </summary>
/// <param name="Payload">Result payload to render.</param>
/// <param name="Kind">Navigation kind.</param>
/// <param name="TargetPath">Optional target path for absolute navigation.</param>
public sealed record ReplNavigationResult(
	object? Payload,
	ReplNavigationKind Kind,
	string? TargetPath = null);
