namespace Repl.Mcp;

/// <summary>
/// Provides direct access to MCP elicitation (structured user input) from the connected client.
/// Inject this interface into command handlers to request user input outside
/// the <see cref="Repl.Interaction.IReplInteractionChannel"/> abstraction.
/// </summary>
/// <remarks>
/// Each method maps to a single-field MCP elicitation request. Returns <c>null</c> when the
/// client does not support elicitation or the user cancels the request.
/// <para>
/// <b>Future:</b> Multi-field elicitation (e.g., a form with several inputs at once) may be
/// added via a builder pattern or a richer interface. If you need this capability, please open
/// a feature request on the Repl repository.
/// </para>
/// </remarks>
public interface IMcpElicitation
{
	/// <summary>
	/// Gets a value indicating whether the connected MCP client supports elicitation.
	/// </summary>
	bool IsSupported { get; }

	/// <summary>
	/// Asks the user for a free-text value.
	/// Returns <c>null</c> when elicitation is not supported or the user cancels.
	/// </summary>
	/// <param name="message">The prompt message shown to the user.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask<string?> ElicitTextAsync(string message, CancellationToken cancellationToken = default);

	/// <summary>
	/// Asks the user for a boolean (yes/no) value.
	/// Returns <c>null</c> when elicitation is not supported or the user cancels.
	/// </summary>
	/// <param name="message">The prompt message shown to the user.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask<bool?> ElicitBooleanAsync(string message, CancellationToken cancellationToken = default);

	/// <summary>
	/// Asks the user to pick one value from a list of choices.
	/// Returns the zero-based index of the selected choice, or <c>null</c> when elicitation
	/// is not supported, the user cancels, or the response does not match any choice.
	/// </summary>
	/// <param name="message">The prompt message shown to the user.</param>
	/// <param name="choices">The available options.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask<int?> ElicitChoiceAsync(string message, IReadOnlyList<string> choices, CancellationToken cancellationToken = default);

	/// <summary>
	/// Asks the user for a numeric value.
	/// Returns <c>null</c> when elicitation is not supported or the user cancels.
	/// </summary>
	/// <param name="message">The prompt message shown to the user.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask<double?> ElicitNumberAsync(string message, CancellationToken cancellationToken = default);
}
