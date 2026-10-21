using System.Text.Json;

namespace Repl.Remote;

/// <summary>A description of commands exposed by an application.</summary>
/// <param name="Name">Application name.</param>
/// <param name="Commands">Discoverable commands.</param>
/// <param name="Contexts">Contexts forming the command hierarchy.</param>
public sealed record RemoteCommandGraph(string Name, IReadOnlyList<RemoteCommand> Commands, IReadOnlyList<RemoteContext> Contexts)
{
	/// <summary>Gets discoverable application global options for structured requests.</summary>
	public IReadOnlyList<RemoteParameter> GlobalParameters { get; init; } = [];
}
