using System.Text.Json;

namespace Repl.Remote;

/// <summary>A context in the command hierarchy.</summary>
/// <param name="Path">Context route template.</param>
/// <param name="Description">User-facing description.</param>
public sealed record RemoteContext(string Path, string? Description);
