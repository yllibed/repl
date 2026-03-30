namespace Repl;

/// <summary>
/// Provides typed access to parsed global option values.
/// Values are updated after each global option parsing pass.
/// </summary>
public interface IGlobalOptionsAccessor
{
	/// <summary>
	/// Gets the typed value of a global option by its registered name.
	/// Returns <paramref name="defaultValue"/> if the option was not provided.
	/// </summary>
	/// <typeparam name="T">Target value type.</typeparam>
	/// <param name="name">The registered option name (without <c>--</c> prefix).</param>
	/// <param name="defaultValue">Value to return if the option was not provided.</param>
	T? GetValue<T>(string name, T? defaultValue = default);

	/// <summary>
	/// Gets all raw string values for a global option (supports repeated options).
	/// Returns an empty list if the option was not provided.
	/// </summary>
	/// <param name="name">The registered option name (without <c>--</c> prefix).</param>
	IReadOnlyList<string> GetRawValues(string name);

	/// <summary>
	/// Returns <see langword="true"/> if the named global option was explicitly provided
	/// in the current invocation.
	/// </summary>
	/// <param name="name">The registered option name (without <c>--</c> prefix).</param>
	bool HasValue(string name);

	/// <summary>
	/// Gets all parsed global option names that have values in the current invocation.
	/// </summary>
	IEnumerable<string> GetOptionNames();
}
