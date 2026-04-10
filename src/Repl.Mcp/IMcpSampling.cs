namespace Repl.Mcp;

/// <summary>
/// Provides direct access to MCP sampling (LLM completions) from the connected client.
/// Inject this interface into command handlers to request completions outside
/// the <see cref="Repl.Interaction.IReplInteractionChannel"/> abstraction.
/// </summary>
public interface IMcpSampling
{
	/// <summary>
	/// Gets a value indicating whether the connected MCP client supports sampling.
	/// </summary>
	bool IsSupported { get; }

	/// <summary>
	/// Requests an LLM completion from the connected agent client.
	/// Returns <c>null</c> when the client does not support sampling.
	/// </summary>
	/// <param name="prompt">The user prompt to send.</param>
	/// <param name="maxTokens">Maximum tokens for the response (default 1024).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask<string?> SampleAsync(
		string prompt,
		int maxTokens = 1024,
		CancellationToken cancellationToken = default);
}
