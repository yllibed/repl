namespace Repl;

/// <summary>
/// Controls how human-readable large results are paged.
/// </summary>
public enum ReplPagerMode
{
	/// <summary>
	/// Let Repl choose the best pager for the active output surface.
	/// </summary>
	Auto,

	/// <summary>
	/// Disable Repl-owned paging.
	/// </summary>
	Off,

	/// <summary>
	/// Use a simple more-style pager.
	/// </summary>
	More,

	/// <summary>
	/// Use an inline pager that redraws in the main terminal buffer.
	/// </summary>
	Inline,

	/// <summary>
	/// Use a full-screen alternate-buffer pager.
	/// </summary>
	Full,
}
