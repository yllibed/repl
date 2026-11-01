namespace Repl.Remote;

/// <summary>Initial terminal metadata; session state remains on the server.</summary>
public sealed record RemoteTerminalOptions
{
	/// <summary>Gets the terminal identity.</summary>
	public string Terminal { get; init; } = "xterm-256color";
	/// <summary>Gets the initial width in character cells.</summary>
	public int Columns { get; init; } = 80;
	/// <summary>Gets the initial height in character cells.</summary>
	public int Rows { get; init; } = 24;
	/// <summary>Gets whether VT output is supported.</summary>
	public bool Ansi { get; init; } = true;

	internal void Validate()
	{
		if (string.IsNullOrWhiteSpace(Terminal)) { throw new InvalidOperationException("Terminal is required."); }
		if (Columns <= 0) { throw new InvalidOperationException("Columns must be positive."); }
		if (Rows <= 0) { throw new InvalidOperationException("Rows must be positive."); }
	}
}
