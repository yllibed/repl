using Repl.Internal.Options;

namespace Repl;

internal sealed class InvocationBindingContext(
	IReadOnlyDictionary<string, string> routeValues,
	IReadOnlyDictionary<string, IReadOnlyList<string>> namedOptions,
	IReadOnlyList<string> positionalArguments,
	OptionSchema optionSchema,
	ReplCaseSensitivity optionCaseSensitivity,
	IReadOnlyList<object?> contextValues,
	IFormatProvider numericFormatProvider,
	IServiceProvider serviceProvider,
	InteractionOptions interactionOptions,
	CancellationToken cancellationToken)
{
	public IReadOnlyDictionary<string, string> RouteValues { get; } = routeValues;

	public IReadOnlyDictionary<string, IReadOnlyList<string>> NamedOptions { get; } = namedOptions;

	public IReadOnlyList<string> PositionalArguments { get; } = positionalArguments;

	public OptionSchema OptionSchema { get; } = optionSchema;

	public ReplCaseSensitivity OptionCaseSensitivity { get; } = optionCaseSensitivity;

	public IReadOnlyList<object?> ContextValues { get; } = contextValues;

	public IFormatProvider NumericFormatProvider { get; } = numericFormatProvider;

	public IServiceProvider ServiceProvider { get; } = serviceProvider;

	public InteractionOptions InteractionOptions { get; } = interactionOptions;

	public CancellationToken CancellationToken { get; } = cancellationToken;
}
