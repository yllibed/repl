using System.Text.Json;

namespace Repl.Remote;

/// <summary>A portable description of a command argument or option.</summary>
/// <param name="Name">Canonical invocation key.</param>
/// <param name="Kind">Either argument or option.</param>
/// <param name="Required">Whether a value is required.</param>
/// <param name="Description">User-facing help.</param>
/// <param name="Schema">JSON Schema Draft 7 describing the value.</param>
/// <param name="Aliases">CLI aliases for this parameter.</param>
public sealed record RemoteParameter(string Name, string Kind, bool Required, string? Description,
	JsonElement Schema, IReadOnlyList<string> Aliases);
