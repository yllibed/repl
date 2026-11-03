using Repl;

namespace Repl.Internal.Options;

internal sealed record OptionSchemaEntry(
	string Token,
	string ParameterName,
	OptionSchemaTokenKind TokenKind,
	ReplArity Arity,
	ReplCaseSensitivity? CaseSensitivity = null,
	string? InjectedValue = null,
	bool IsHidden = false)
{
	/// <summary>This entry's own case sensitivity, or <paramref name="global"/> when it sets none.</summary>
	public ReplCaseSensitivity EffectiveCaseSensitivity(ReplCaseSensitivity global) => CaseSensitivity ?? global;
}
