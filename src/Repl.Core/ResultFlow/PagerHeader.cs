namespace Repl;

internal sealed record PagerHeader(IReadOnlyList<string> Lines, IReadOnlySet<string> NormalizedLines)
{
	public static PagerHeader Empty { get; } = new([], new HashSet<string>(StringComparer.Ordinal));
}
