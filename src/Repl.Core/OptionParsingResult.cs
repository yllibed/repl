namespace Repl;

internal sealed class OptionParsingResult(
	IReadOnlyDictionary<string, IReadOnlyList<string>> namedOptions,
	IReadOnlyList<string> positionalArguments)
{
	public IReadOnlyDictionary<string, IReadOnlyList<string>> NamedOptions { get; } = namedOptions;

	public IReadOnlyList<string> PositionalArguments { get; } = positionalArguments;
}
