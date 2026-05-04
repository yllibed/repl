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
	/// Use an interactive scrolling pager.
	/// </summary>
	Scroll,

	/// <summary>
	/// Use an external pager process when available.
	/// </summary>
	External,
}
