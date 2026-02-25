namespace Repl;

/// <summary>
/// Provides mutable per-session state for command handlers and ambient commands.
/// </summary>
public interface IReplSessionState
{
	/// <summary>
	/// Tries to read a typed value from the session state.
	/// </summary>
	/// <typeparam name="T">Expected value type.</typeparam>
	/// <param name="key">State key.</param>
	/// <param name="value">Resolved value when present.</param>
	/// <returns>True when the key exists and value type matches.</returns>
	bool TryGet<T>(string key, out T? value);

	/// <summary>
	/// Reads a typed value from the session state.
	/// </summary>
	/// <typeparam name="T">Expected value type.</typeparam>
	/// <param name="key">State key.</param>
	/// <returns>The value, or default when not present.</returns>
	T? Get<T>(string key);

	/// <summary>
	/// Stores a typed value in the session state.
	/// </summary>
	/// <typeparam name="T">Value type.</typeparam>
	/// <param name="key">State key.</param>
	/// <param name="value">Value to store.</param>
	void Set<T>(string key, T value);

	/// <summary>
	/// Removes a value from the session state.
	/// </summary>
	/// <param name="key">State key.</param>
	/// <returns>True when a value was removed.</returns>
	bool Remove(string key);

	/// <summary>
	/// Clears all values from the session state.
	/// </summary>
	void Clear();
}
