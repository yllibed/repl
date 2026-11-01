using System.Text.Json;

namespace Repl.Remote;

/// <summary>The final outcome of one command, distinct from an RPC failure.</summary>
/// <param name="ExitCode">The application-selected exit code.</param>
/// <param name="Kind">The portable outcome category.</param>
/// <param name="Result">The normalized command result, when available.</param>
/// <param name="StandardOutput">Text written to the invocation's output stream.</param>
/// <param name="StandardError">Text written to the invocation's error stream.</param>
public sealed record RemoteExecutionResult(int ExitCode, string Kind, JsonElement? Result = null,
	string StandardOutput = "", string StandardError = "");
