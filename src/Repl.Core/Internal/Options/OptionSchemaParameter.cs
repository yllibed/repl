using Repl;

namespace Repl.Internal.Options;

internal sealed record OptionSchemaParameter(
	string Name,
	Type ParameterType,
	ReplParameterMode Mode,
	ReplCaseSensitivity? CaseSensitivity = null,
	ReplArity? ExplicitArity = null,
	bool IsHidden = false,
	bool IsAutomationHidden = false,
	// Whether a caller may leave the option out entirely. Distinct from Arity, which counts the
	// values a token consumes: a bool flag is ZeroOrOne yet a non-nullable bool with no default
	// still fails to bind when absent, so arity alone cannot answer "is this option mandatory".
	bool CanBeOmitted = true,
	// Direct handler parameters may fall back to DI after named binding; options-group properties
	// never do, because the binder constructs the group before reaching its service fallback.
	bool SupportsServiceFallback = false,
	// Whether the option is an options-group property rather than a handler parameter of the same name: a
	// group parameter can be named like one of its own properties.
	bool IsGroupProperty = false);
