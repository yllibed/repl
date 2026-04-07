namespace Repl.Interaction;

/// <summary>
/// Result of an <see cref="IReplInteractionHandler.TryHandleAsync"/> call.
/// Either <see cref="Handled"/> is <c>true</c> and <see cref="Value"/> contains the result,
/// or <see cref="Handled"/> is <c>false</c> and the pipeline moves to the next handler.
/// </summary>
public readonly struct InteractionResult
{
	/// <summary>
	/// Sentinel value indicating the handler did not handle the request.
	/// </summary>
	public static readonly InteractionResult Unhandled;

	/// <summary>
	/// Creates a successful result with the given value.
	/// </summary>
	/// <param name="value">The interaction result value.</param>
	/// <returns>A handled <see cref="InteractionResult"/>.</returns>
	public static InteractionResult Success(object? value) => new(handled: true, value: value);

	private InteractionResult(bool handled, object? value)
	{
		Handled = handled;
		Value = value;
	}

	/// <summary>
	/// Gets whether the handler handled the request.
	/// </summary>
	public bool Handled { get; }

	/// <summary>
	/// Gets the result value. Only meaningful when <see cref="Handled"/> is <c>true</c>.
	/// </summary>
	public object? Value { get; }
}
