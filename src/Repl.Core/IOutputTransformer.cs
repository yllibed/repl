namespace Repl;

/// <summary>
/// Transforms a logical value into a target output format.
/// </summary>
public interface IOutputTransformer
{
	/// <summary>
	/// Gets the transformer format name.
	/// </summary>
	string Name { get; }

	/// <summary>
	/// Transforms a value to the target representation.
	/// </summary>
	/// <param name="value">Input value.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Transformed payload as text.</returns>
	ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default);
}