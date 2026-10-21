using System.Text.Json;

namespace Repl.Remote;

/// <summary>A command submitted as tokens or as a canonical route with JSON values.</summary>
public sealed record RemoteInvocationRequest
{
	/// <summary>Gets CLI tokens; mutually exclusive with <see cref="Command"/>.</summary>
	public string[]? Arguments { get; init; }
	/// <summary>Gets the registered route template for a structured invocation.</summary>
	public string? Command { get; init; }
	/// <summary>Gets values keyed by canonical parameter name.</summary>
	public IReadOnlyDictionary<string, JsonElement>? Values { get; init; }
	/// <summary>Gets global option values for a structured invocation.</summary>
	public IReadOnlyDictionary<string, JsonElement>? GlobalValues { get; init; }

	internal void Validate()
	{
		if ((Arguments is null) == (Command is null))
			throw new InvalidOperationException("Supply either arguments or command.");
		if (Command is not null && string.IsNullOrWhiteSpace(Command))
			throw new InvalidOperationException("Command cannot be empty.");
		if (Arguments is not null && (Arguments.Any(argument => argument is null) || Values is not null || GlobalValues is not null))
			throw new InvalidOperationException("Token requests cannot contain null tokens or structured values.");
	}
}
