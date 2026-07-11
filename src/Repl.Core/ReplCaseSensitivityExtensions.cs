namespace Repl;

/// <summary>
/// Shared mapping from <see cref="ReplCaseSensitivity"/> to string comparison primitives —
/// one place instead of a ternary re-derived at every call site.
/// </summary>
internal static class ReplCaseSensitivityExtensions
{
	internal static StringComparison ToStringComparison(this ReplCaseSensitivity caseSensitivity) =>
		caseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
}
