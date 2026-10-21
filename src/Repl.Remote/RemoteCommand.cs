using System.Text.Json;

namespace Repl.Remote;

/// <summary>A command's canonical identity and discoverable parameters.</summary>
/// <param name="Path">Registered route template.</param>
/// <param name="Description">User-facing description.</param>
/// <param name="Aliases">CLI aliases.</param>
/// <param name="Parameters">Arguments and options in declaration order.</param>
public sealed record RemoteCommand(string Path, string? Description, IReadOnlyList<string> Aliases,
	IReadOnlyList<RemoteParameter> Parameters);
