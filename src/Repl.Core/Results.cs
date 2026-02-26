namespace Repl;

/// <summary>
/// Result factory helpers.
/// </summary>
public static class Results
{
	/// <summary>
	/// Creates a conventional success text result.
	/// </summary>
	/// <param name="content">Text content.</param>
	/// <returns>A text result.</returns>
	public static IReplResult Ok(string content) => Text(content);

	/// <summary>
	/// Creates a structured success result.
	/// </summary>
	/// <param name="message">Success message.</param>
	/// <param name="details">Optional details payload.</param>
	/// <returns>A success result.</returns>
	public static IReplResult Success(string message, object? details = null) =>
		new ReplResult("success", Code: null, Message: message, Details: details);

	/// <summary>
	/// Requests interactive navigation one level up after command completion.
	/// </summary>
	/// <param name="payload">Payload to render.</param>
	/// <returns>A navigation result.</returns>
	public static ReplNavigationResult NavigateUp(object? payload = null) =>
		new(payload, ReplNavigationKind.Up);

	/// <summary>
	/// Requests interactive navigation to an explicit scope path after command completion.
	/// </summary>
	/// <param name="targetPath">Target scope path.</param>
	/// <param name="payload">Payload to render.</param>
	/// <returns>A navigation result.</returns>
	public static ReplNavigationResult NavigateTo(string targetPath, object? payload = null)
	{
		targetPath = string.IsNullOrWhiteSpace(targetPath)
			? throw new ArgumentException("Target path cannot be empty.", nameof(targetPath))
			: targetPath;
		return new ReplNavigationResult(payload, ReplNavigationKind.To, targetPath);
	}

	/// <summary>
	/// Creates a plain text result.
	/// </summary>
	/// <param name="content">Text content.</param>
	/// <returns>A text result.</returns>
	public static IReplResult Text(string content) =>
		new ReplResult("text", Code: null, Message: content, Details: null);

	/// <summary>
	/// Creates an error result.
	/// </summary>
	/// <param name="code">Error code.</param>
	/// <param name="message">Error message.</param>
	/// <returns>An error result.</returns>
	public static IReplResult Error(string code, string message) =>
		new ReplResult("error", Code: code, Message: message, Details: null);

	/// <summary>
	/// Creates a validation result.
	/// </summary>
	/// <param name="message">Validation message.</param>
	/// <param name="details">Optional details payload.</param>
	/// <returns>A validation result.</returns>
	public static IReplResult Validation(string message, object? details = null) =>
		new ReplResult("validation", Code: null, Message: message, Details: details);

	/// <summary>
	/// Creates a not-found result.
	/// </summary>
	/// <param name="message">Not-found message.</param>
	/// <returns>A not-found result.</returns>
	public static IReplResult NotFound(string message) =>
		new ReplResult("not_found", Code: null, Message: message, Details: null);

	/// <summary>
	/// Creates a cancelled result (e.g. user declined a confirmation prompt).
	/// </summary>
	/// <param name="message">Cancellation message.</param>
	/// <returns>A cancelled result.</returns>
	public static IReplResult Cancelled(string message) =>
		new ReplResult("cancelled", Code: null, Message: message, Details: null);

	/// <summary>
	/// Creates an explicit process exit result with an optional payload to render.
	/// </summary>
	/// <param name="code">Process exit code.</param>
	/// <param name="payload">Optional payload rendered through configured output format.</param>
	/// <returns>An explicit exit result.</returns>
	public static IExitResult Exit(int code, object? payload = null) =>
		new ExitResult(code, payload);
}
