using System.Globalization;

namespace Repl;

/// <summary>
/// Parsing configuration.
/// </summary>
public sealed class ParsingOptions
{
	private static readonly HashSet<string> ReservedConstraintNames =
	[
		"string",
		"alpha",
		"bool",
		"email",
		"uri",
		"url",
		"urn",
		"time",
		"timeonly",
		"time-only",
		"date",
		"dateonly",
		"date-only",
		"datetime",
		"date-time",
		"datetimeoffset",
		"date-time-offset",
		"timespan",
		"time-span",
		"guid",
		"long",
		"int",
	];
	private readonly Dictionary<string, Func<string, bool>> _customRouteConstraints =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, GlobalOptionDefinition> _globalOptions =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Gets or sets a value indicating whether unknown options are allowed.
	/// </summary>
	public bool AllowUnknownOptions { get; set; }

	/// <summary>
	/// Gets or sets option-name case-sensitivity mode.
	/// </summary>
	public ReplCaseSensitivity OptionCaseSensitivity { get; set; } = ReplCaseSensitivity.CaseSensitive;

	/// <summary>
	/// Gets or sets a value indicating whether response files (for example: <c>@args.rsp</c>) are expanded.
	/// </summary>
	public bool AllowResponseFiles { get; set; } = true;

	/// <summary>
	/// Gets or sets the culture mode used for numeric conversions.
	/// </summary>
	public NumericParsingCulture NumericCulture { get; set; } = NumericParsingCulture.Invariant;

	internal IReadOnlyDictionary<string, GlobalOptionDefinition> GlobalOptions => _globalOptions;

	internal IFormatProvider NumericFormatProvider => NumericCulture == NumericParsingCulture.Current
		? CultureInfo.CurrentCulture
		: CultureInfo.InvariantCulture;

	/// <summary>
	/// Registers a custom route constraint predicate.
	/// </summary>
	/// <param name="name">Constraint name.</param>
	/// <param name="predicate">Predicate used to validate route segment input.</param>
	public void AddRouteConstraint(string name, Func<string, bool> predicate)
	{
		name = string.IsNullOrWhiteSpace(name)
			? throw new ArgumentException("Constraint name cannot be empty.", nameof(name))
			: name;
		ArgumentNullException.ThrowIfNull(predicate);

		if (ReservedConstraintNames.Contains(name))
		{
			throw new InvalidOperationException(
				$"Route constraint '{name}' is reserved by a built-in constraint.");
		}

		if (_customRouteConstraints.ContainsKey(name))
		{
			throw new InvalidOperationException(
				$"A custom route constraint named '{name}' is already registered.");
		}

		_customRouteConstraints[name] = predicate;
	}

	internal bool TryGetRouteConstraint(string name, out Func<string, bool> predicate) =>
		_customRouteConstraints.TryGetValue(name, out predicate!);

	/// <summary>
	/// Registers a custom global option consumed before command routing.
	/// </summary>
	/// <typeparam name="T">Declared value type (metadata only for now).</typeparam>
	/// <param name="name">Canonical name without prefix (for example: "tenant").</param>
	/// <param name="aliases">Optional aliases. Values without prefix are normalized to <c>--alias</c>.</param>
	/// <param name="defaultValue">Optional default value metadata.</param>
	public void AddGlobalOption<T>(string name, string[]? aliases = null, T? defaultValue = default)
	{
		name = string.IsNullOrWhiteSpace(name)
			? throw new ArgumentException("Global option name cannot be empty.", nameof(name))
			: name.Trim();

		var normalizedCanonical = NormalizeLongToken(name);
		if (_globalOptions.ContainsKey(name))
		{
			throw new InvalidOperationException($"A global option named '{name}' is already registered.");
		}

		var normalizedAliases = (aliases ?? [])
			.Where(alias => !string.IsNullOrWhiteSpace(alias))
			.Select(alias => NormalizeAliasToken(alias.Trim()))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Where(alias => !string.Equals(alias, normalizedCanonical, StringComparison.OrdinalIgnoreCase))
			.ToArray();

		_globalOptions[name] = new GlobalOptionDefinition(
			Name: name,
			CanonicalToken: normalizedCanonical,
			Aliases: normalizedAliases,
			DefaultValue: defaultValue?.ToString());
	}

	private static string NormalizeLongToken(string name) =>
		name.StartsWith("--", StringComparison.Ordinal)
			? name
			: $"--{name}";

	private static string NormalizeAliasToken(string alias)
	{
		if (alias.StartsWith("--", StringComparison.Ordinal) || alias.StartsWith('-'))
		{
			return alias;
		}

		return $"--{alias}";
	}
}
