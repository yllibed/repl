using Repl;

namespace Repl.Internal.Options;

internal sealed record OptionSchemaEntry(
	string Token,
	string ParameterName,
	OptionSchemaTokenKind TokenKind,
	ReplArity Arity,
	ReplCaseSensitivity? CaseSensitivity = null,
	string? InjectedValue = null);
