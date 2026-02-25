namespace Repl;

/// <summary>
/// Cursor over a snapshot of history entries for Up/Down arrow navigation.
/// </summary>
internal sealed class HistoryNavigator
{
	private readonly string[] _entries;
	private int _index;

	/// <summary>
	/// Initializes a new instance with a snapshot of history entries (oldest to newest).
	/// An empty sentinel is appended at the end so Down from the last entry returns to a blank line.
	/// </summary>
	internal HistoryNavigator(IReadOnlyList<string> entries)
	{
		_entries = new string[entries.Count + 1];
		for (var i = 0; i < entries.Count; i++)
		{
			_entries[i] = entries[i];
		}

		_entries[entries.Count] = string.Empty;
		_index = _entries.Length - 1;
	}

	/// <summary>
	/// Saves the current typed text at the current index so it can be restored if the user navigates back.
	/// </summary>
	internal void UpdateCurrent(string text)
	{
		_entries[_index] = text;
	}

	/// <summary>
	/// Moves up (toward older entries). Returns true if the cursor moved.
	/// </summary>
	internal bool TryMoveUp(out string entry)
	{
		if (_index > 0)
		{
			_index--;
			entry = _entries[_index];
			return true;
		}

		entry = string.Empty;
		return false;
	}

	/// <summary>
	/// Moves down (toward newer entries). Returns true if the cursor moved.
	/// </summary>
	internal bool TryMoveDown(out string entry)
	{
		if (_index < _entries.Length - 1)
		{
			_index++;
			entry = _entries[_index];
			return true;
		}

		entry = string.Empty;
		return false;
	}
}
