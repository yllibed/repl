namespace Repl;

internal sealed record OptionSchemaParameter(
	string Name,
	Type ParameterType,
	ReplParameterMode Mode,
	ReplCaseSensitivity? CaseSensitivity = null);
