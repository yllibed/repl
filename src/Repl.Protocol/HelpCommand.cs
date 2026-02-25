namespace Repl.Protocol;

/// <summary>
/// Machine-readable command descriptor.
/// </summary>
/// <param name="Name">Command name.</param>
/// <param name="Description">Command description.</param>
/// <param name="Usage">Usage template.</param>
public sealed record HelpCommand(
	string Name,
	string Description,
	string Usage);