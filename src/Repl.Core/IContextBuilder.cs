namespace Repl;

/// <summary>
/// Fluent context builder returned by <c>Context(...)</c> mappings.
/// Supports context-level metadata while keeping standard mapping operations available.
/// </summary>
public interface IContextBuilder : IReplMap
{
	/// <summary>
	/// Marks a context as hidden or visible in discovery surfaces.
	/// Hidden contexts remain routable when addressed explicitly.
	/// </summary>
	/// <param name="isHidden">True to hide the context from discovery output.</param>
	/// <returns>The same context builder.</returns>
	IContextBuilder Hidden(bool isHidden = true);

	/// <summary>
	/// Sets a rich markdown description for the context.
	/// Used for agent tool descriptions and documentation export.
	/// </summary>
	/// <param name="markdown">Markdown content.</param>
	/// <returns>The same context builder.</returns>
	IContextBuilder WithDetails(string markdown);
}
