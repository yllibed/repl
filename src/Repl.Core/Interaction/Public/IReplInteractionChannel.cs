namespace Repl.Interaction;

/// <summary>
/// Provides bidirectional interaction during command execution.
/// </summary>
public interface IReplInteractionChannel
{
	/// <summary>
	/// Writes progress information.
	/// </summary>
	/// <param name="label">Progress label.</param>
	/// <param name="percent">Optional progress percent.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>An asynchronous operation.</returns>
	ValueTask WriteProgressAsync(
		string label,
		double? percent,
		CancellationToken cancellationToken);

	/// <summary>
	/// Writes a status line.
	/// </summary>
	/// <param name="text">Status text.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>An asynchronous operation.</returns>
	ValueTask WriteStatusAsync(
		string text,
		CancellationToken cancellationToken);

	/// <summary>
	/// Prompts the user for one option from a choice list.
	/// </summary>
	/// <param name="name">Prompt name.</param>
	/// <param name="prompt">Prompt text.</param>
	/// <param name="choices">Selectable choices.</param>
	/// <param name="defaultIndex">Index of the default choice returned on empty input, or null to use the first choice.</param>
	/// <param name="options">Optional ask options (cancellation, timeout). When null or token is default, uses the ambient per-command token.</param>
	/// <returns>The zero-based index of the selected choice.</returns>
	ValueTask<int> AskChoiceAsync(
		string name,
		string prompt,
		IReadOnlyList<string> choices,
		int? defaultIndex = null,
		AskOptions? options = null);

	/// <summary>
	/// Prompts the user for confirmation.
	/// </summary>
	///
	/// <param name="name">Prompt name.</param>
	/// <param name="prompt">Prompt text.</param>
	/// <param name="defaultValue">Default choice.</param>
	/// <param name="options">Optional ask options (cancellation, timeout). When null or token is default, uses the ambient per-command token.</param>
	/// <returns>True when confirmed.</returns>
	ValueTask<bool> AskConfirmationAsync(
		string name,
		string prompt,
		bool defaultValue = false,
		AskOptions? options = null);

	/// <summary>
	/// Prompts the user for free-form text.
	/// </summary>
	/// <param name="name">Prompt name.</param>
	/// <param name="prompt">Prompt text.</param>
	/// <param name="defaultValue">Optional default value.</param>
	/// <param name="options">Optional ask options (cancellation, timeout). When null or token is default, uses the ambient per-command token.</param>
	/// <returns>The captured text.</returns>
	ValueTask<string> AskTextAsync(
		string name,
		string prompt,
		string? defaultValue = null,
		AskOptions? options = null);
}
