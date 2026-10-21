using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
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
	// Definitions, case sensitivity, and derived token ownership are published together. Every
	// update copies the definitions and swaps one immutable snapshot by CAS, so readers never
	// enumerate a Dictionary while a writer mutates it and concurrent writers cannot lose updates.
	private GlobalOptionConfigurationSnapshot _globalOptionState = CreateGlobalOptionConfigurationSnapshot(
		ReplCaseSensitivity.CaseSensitive,
		new Dictionary<string, GlobalOptionDefinition>(StringComparer.OrdinalIgnoreCase));
	private Action<ReplCaseSensitivity>? _validateOptionCaseSensitivityChange;
	private Action? _globalDiscoveryChanged;
	private object? _configurationMutationGate;

	/// <summary>
	/// Gets or sets a value indicating whether unknown options are allowed.
	/// </summary>
	public bool AllowUnknownOptions { get; set; }

	/// <summary>
	/// Gets or sets option-name case-sensitivity mode.
	/// </summary>
	public ReplCaseSensitivity OptionCaseSensitivity
	{
		get => Volatile.Read(ref _globalOptionState).CaseSensitivity;
		set => UpdateGlobalOptionConfigurationSnapshot(state =>
		{
			if (state.CaseSensitivity == value)
			{
				return state;
			}

			_validateOptionCaseSensitivityChange?.Invoke(value);
			return CreateGlobalOptionConfigurationSnapshot(value, CloneDefinitions(state));
		});
	}

	/// <summary>
	/// Gets or sets a value indicating whether response files (for example: <c>@args.rsp</c>) are expanded.
	/// </summary>
	public bool AllowResponseFiles { get; set; } = true;

	/// <summary>
	/// Gets or sets the culture mode used for numeric conversions.
	/// </summary>
	public NumericParsingCulture NumericCulture { get; set; } = NumericParsingCulture.Invariant;

	internal IReadOnlyDictionary<string, GlobalOptionDefinition> GlobalOptions =>
		CaptureGlobalOptionConfiguration().Definitions;

	internal GlobalOptionConfigurationSnapshot CaptureGlobalOptionConfiguration() =>
		Volatile.Read(ref _globalOptionState);

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
	/// <typeparam name="T">Declared value type.</typeparam>
	/// <param name="name">Canonical name without prefix (for example: "tenant").</param>
	/// <param name="aliases">Optional aliases. Values without prefix are normalized to <c>--alias</c>.</param>
	/// <param name="defaultValue">Optional default value metadata.</param>
	public void AddGlobalOption<T>(string name, string[]? aliases = null, T? defaultValue = default) =>
		AddGlobalOptionCore(name, typeof(T), aliases, FormatGlobalDefaultValue(defaultValue, typeof(T)));

	/// <summary>
	/// Registers a custom global option with an explicit help description.
	/// </summary>
	/// <remarks>
	/// <paramref name="description"/> is the trailing required parameter so this overload never collides with
	/// <see cref="AddGlobalOption{T}(string, string[], T)"/> during overload resolution: a positional
	/// <c>null</c> second argument (for example <c>AddGlobalOption&lt;string&gt;("tenant", null)</c>) binds
	/// unambiguously to the aliases-only overload. The typed <see cref="System.ComponentModel.DescriptionAttribute"/>
	/// path (<c>UseGlobalOptions&lt;T&gt;()</c>) remains the primary way to attach descriptions.
	/// </remarks>
	/// <typeparam name="T">Declared value type.</typeparam>
	/// <param name="name">Canonical name without prefix (for example: "tenant").</param>
	/// <param name="aliases">Aliases (pass <c>null</c> for none). Values without prefix are normalized to <c>--alias</c>.</param>
	/// <param name="defaultValue">Default value metadata (pass <c>default</c> for none).</param>
	/// <param name="description">Description shown in help output.</param>
	public void AddGlobalOption<T>(string name, string[]? aliases, T? defaultValue, string description) =>
		AddGlobalOptionCore(name, typeof(T), aliases, FormatGlobalDefaultValue(defaultValue, typeof(T)), description);

	/// <summary>
	/// Registers a custom global option using a type or constraint name
	/// (for example: "int", "guid", "bool", or a registered custom route constraint name).
	/// </summary>
	/// <param name="name">Canonical name without prefix (for example: "tenant").</param>
	/// <param name="constraintOrTypeName">
	/// Built-in type name ("string", "int", "long", "bool", "guid", "uri", "date", "datetime", "timespan")
	/// or a registered custom route constraint name. Custom constraints resolve to <c>string</c>.
	/// </param>
	/// <param name="aliases">Optional aliases. Values without prefix are normalized to <c>--alias</c>.</param>
	/// <param name="defaultValue">Optional default value as string.</param>
	public void AddGlobalOption(string name, string constraintOrTypeName, string[]? aliases = null, string? defaultValue = null) =>
		AddGlobalOptionCore(name, ResolveConstraintOrTypeName(constraintOrTypeName, _customRouteConstraints), aliases, defaultValue);

	/// <summary>
	/// Registers a custom global option (by type or constraint name) with an explicit help description.
	/// </summary>
	/// <remarks>
	/// <paramref name="description"/> is the trailing required parameter so this overload never collides with
	/// <see cref="AddGlobalOption(string, string, string[], string)"/> during overload resolution: a positional
	/// <c>null</c> third argument (for example <c>AddGlobalOption("port", "int", null)</c>) binds unambiguously
	/// to the aliases-only overload.
	/// </remarks>
	/// <param name="name">Canonical name without prefix (for example: "tenant").</param>
	/// <param name="constraintOrTypeName">
	/// Built-in type name ("string", "int", "long", "bool", "guid", "uri", "date", "datetime", "timespan")
	/// or a registered custom route constraint name. Custom constraints resolve to <c>string</c>.
	/// </param>
	/// <param name="aliases">Aliases (pass <c>null</c> for none). Values without prefix are normalized to <c>--alias</c>.</param>
	/// <param name="defaultValue">Default value as string (pass <c>null</c> for none).</param>
	/// <param name="description">Description shown in help output.</param>
	public void AddGlobalOption(string name, string constraintOrTypeName, string[]? aliases, string? defaultValue, string description) =>
		AddGlobalOptionCore(name, ResolveConstraintOrTypeName(constraintOrTypeName, _customRouteConstraints), aliases, defaultValue, description);

	/// <summary>
	/// Selects a registered global option for discovery metadata configuration.
	/// </summary>
	/// <param name="name">Canonical option name, with or without the <c>--</c> prefix.</param>
	/// <returns>A global-option metadata builder.</returns>
	/// <exception cref="KeyNotFoundException">No global option with the supplied canonical name is registered.</exception>
	public GlobalOptionBuilder GlobalOption(string name)
	{
		var requested = string.IsNullOrWhiteSpace(name)
			? throw new ArgumentException("Global option name cannot be empty.", nameof(name))
			: name.Trim();

		return new GlobalOptionBuilder(this, ResolveGlobalOptionKey(requested));
	}

	private string ResolveGlobalOptionKey(string requested)
	{
		var definitions = Volatile.Read(ref _globalOptionState).Definitions;
		// Keyed probe first; the scan below only covers a caller passing the rendered token
		// ("--tenant") for an option registered under its bare name, or the reverse.
		if (definitions.ContainsKey(requested))
		{
			return requested;
		}

		var canonicalToken = NormalizeLongToken(requested);

		return definitions.Values.FirstOrDefault(option =>
			string.Equals(option.CanonicalToken, canonicalToken, StringComparison.OrdinalIgnoreCase))
			?.Name
			?? throw new KeyNotFoundException($"No global option named '{requested}' is registered.");
	}

	// Re-reads the entry instead of closing over the definition the builder was created from: a
	// retained builder must not write back a stale record. Not reachable today, since duplicate
	// registration throws, but the closure form was one added mutator away from silently reverting.
	internal void SetGlobalOptionHidden(string canonicalName, bool isHidden) =>
		UpdateGlobalOptionConfigurationSnapshot(state =>
		{
			if (!state.Definitions.TryGetValue(canonicalName, out var definition)
				|| definition.IsHidden == isHidden)
			{
				return state;
			}

			var definitions = CloneDefinitions(state);
			definitions[canonicalName] = definition with { IsHidden = isHidden };
			return CreateGlobalOptionConfigurationSnapshot(state.CaseSensitivity, definitions);
		});

	internal void SetGlobalOptionAliasHidden(string canonicalName, string alias, bool isHidden) =>
		UpdateGlobalOptionConfigurationSnapshot(state =>
		{
			if (!state.Definitions.TryGetValue(canonicalName, out var definition))
			{
				return state;
			}

			var normalizedAlias = NormalizeAliasToken(alias.Trim());
			var comparer = ResolveOptionTokenComparer(state.CaseSensitivity);
			var registeredAlias = definition.Aliases.FirstOrDefault(candidate =>
				string.Equals(candidate, normalizedAlias, StringComparison.Ordinal));
			if (registeredAlias is null)
			{
				var matches = definition.Aliases.Where(candidate => comparer.Equals(candidate, normalizedAlias)).ToArray();
				registeredAlias = matches.Length switch
				{
					0 => throw new KeyNotFoundException(
						$"No alias token '{alias}' is registered for global option '{canonicalName}'."),
					1 => matches[0],
					_ => throw new InvalidOperationException(
						$"Alias token '{alias}' is ambiguous for global option '{canonicalName}' because registered aliases differ only by casing."),
				};
			}

			var hiddenAliases = definition.HiddenAliases.ToHashSet(StringComparer.Ordinal);
			var changed = isHidden
				? hiddenAliases.Add(registeredAlias)
				: hiddenAliases.RemoveWhere(candidate => comparer.Equals(candidate, registeredAlias)) > 0;
			if (!changed)
			{
				return state;
			}

			var definitions = CloneDefinitions(state);
			definitions[canonicalName] = definition with { HiddenAliases = [.. hiddenAliases] };
			return CreateGlobalOptionConfigurationSnapshot(state.CaseSensitivity, definitions);
		});

	// The canonical token's visibility is governed solely by the definition's own IsHidden, never
	// by HiddenAliases: a case-distinct hidden alias (registered while case-sensitive) can become
	// equivalent to the canonical token once parsing switches to case-insensitive, and without this
	// guard that would incorrectly hide the canonical spelling too.
	//
	// The canonical check is deliberately Ordinal, not the effective comparer: the effective
	// comparer is exactly what makes "--TENANT" equivalent to canonical "--tenant" once parsing
	// turns case-insensitive, and using it here would ALSO exempt that distinct alias string from
	// its own HiddenAliases membership — the opposite of what HiddenAlias("--TENANT") asked for.
	// Ordinal identifies only the canonical spelling itself; every other alias string still falls
	// through to the normal effective-comparer membership check below.
	internal bool IsGlobalOptionAliasHidden(GlobalOptionDefinition definition, string token) =>
		CaptureGlobalOptionConfiguration().IsAliasHidden(definition, token);

	private StringComparer ResolveOptionTokenComparer() =>
		ResolveOptionTokenComparer(OptionCaseSensitivity);

	private static StringComparer ResolveOptionTokenComparer(ReplCaseSensitivity caseSensitivity) =>
		caseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;

	// Kept as a distinct six-parameter signature, not folded into the overload below as a trailing
	// optional parameter. Optional parameters are a compile-time convenience: the call site emits
	// the full signature, so folding would rename the method as far as the CLR is concerned.
	// Repl.Defaults ships as its own package and declares `Repl.Core >= <its own version>` — a
	// minimum, not an exact pin — so a consumer can legitimately run an older compiled
	// Repl.Defaults against a newer Repl.Core. That binary calls this arity; removing it would
	// raise MissingMethodException at runtime rather than fail anyone's build.
	internal void AddGlobalOptionCore(
		string name,
		Type valueType,
		string[]? aliases,
		string? defaultValue,
		string? description = null,
		Type? ownerType = null) =>
		AddGlobalOptionCore(name, valueType, aliases, defaultValue, description, ownerType, isHidden: false);

	internal void AddGlobalOptionCore(
		string name,
		Type valueType,
		string[]? aliases,
		string? defaultValue,
		string? description,
		Type? ownerType,
		bool isHidden)
	{
		name = string.IsNullOrWhiteSpace(name)
			? throw new ArgumentException("Global option name cannot be empty.", nameof(name))
			: name.Trim();
		var normalizedCanonical = NormalizeLongToken(name);

		UpdateGlobalOptionConfigurationSnapshot(state =>
		{
			if (state.Definitions.TryGetValue(name, out var existing))
			{
				throw new InvalidOperationException(BuildDuplicateGlobalOptionMessage(name, existing.OwnerType, ownerType));
			}

			var canonicalCollision = state.Definitions.Values.FirstOrDefault(candidate =>
				string.Equals(candidate.CanonicalToken, normalizedCanonical, StringComparison.OrdinalIgnoreCase));
			if (canonicalCollision is not null)
			{
				throw new InvalidOperationException(
					$"A global option named '{canonicalCollision.Name}' already renders as the same token "
					+ $"'{normalizedCanonical}' that registering '{name}' would produce.");
			}

			var tokenComparer = ResolveOptionTokenComparer(state.CaseSensitivity);
			var normalizedAliases = (aliases ?? [])
				.Where(alias => !string.IsNullOrWhiteSpace(alias))
				.Select(alias => NormalizeAliasToken(alias.Trim()))
				.Distinct(tokenComparer)
				.Where(alias => !tokenComparer.Equals(alias, normalizedCanonical))
				.ToArray();
			var definitions = CloneDefinitions(state);
			definitions[name] = new GlobalOptionDefinition(
				Name: name,
				CanonicalToken: normalizedCanonical,
				Aliases: normalizedAliases,
				DefaultValue: defaultValue,
				Description: description,
				ValueType: valueType,
				OwnerType: ownerType,
				IsHidden: isHidden,
				HiddenAliases: []);
			return CreateGlobalOptionConfigurationSnapshot(state.CaseSensitivity, definitions);
		});
	}

	// The last-registered definition wins a token/alias collision. Ownership is derived while the
	// private definitions copy is still mutable, then both are wrapped and published atomically.
	internal IReadOnlyDictionary<string, GlobalOptionDefinition> ResolveCustomTokenOwnership() =>
		CaptureGlobalOptionConfiguration().Ownership;

	internal void ConfigureGlobalDiscoveryMutationCallbacks(
		Action<ReplCaseSensitivity> validateOptionCaseSensitivityChange,
		Action globalDiscoveryChanged,
		object configurationMutationGate)
	{
		ArgumentNullException.ThrowIfNull(validateOptionCaseSensitivityChange);
		ArgumentNullException.ThrowIfNull(globalDiscoveryChanged);
		ArgumentNullException.ThrowIfNull(configurationMutationGate);
		_validateOptionCaseSensitivityChange = validateOptionCaseSensitivityChange;
		_globalDiscoveryChanged = globalDiscoveryChanged;
		_configurationMutationGate = configurationMutationGate;
	}

	private void UpdateGlobalOptionConfigurationSnapshot(Func<GlobalOptionConfigurationSnapshot, GlobalOptionConfigurationSnapshot> update)
	{
		var changed = _configurationMutationGate is { } gate
			? UpdateGlobalOptionConfigurationSnapshotUnderGate(update, gate)
			: TryUpdateGlobalOptionConfigurationSnapshot(update);
		if (changed)
		{
			_globalDiscoveryChanged?.Invoke();
		}
	}

	private bool UpdateGlobalOptionConfigurationSnapshotUnderGate(
		Func<GlobalOptionConfigurationSnapshot, GlobalOptionConfigurationSnapshot> update,
		object gate)
	{
		lock (gate)
		{
			return TryUpdateGlobalOptionConfigurationSnapshot(update);
		}
	}

	private bool TryUpdateGlobalOptionConfigurationSnapshot(Func<GlobalOptionConfigurationSnapshot, GlobalOptionConfigurationSnapshot> update)
	{
		while (true)
		{
			var current = Volatile.Read(ref _globalOptionState);
			var updated = update(current);
			if (ReferenceEquals(current, updated))
			{
				return false;
			}

			if (ReferenceEquals(Interlocked.CompareExchange(ref _globalOptionState, updated, current), current))
			{
				return true;
			}
		}
	}

	private static Dictionary<string, GlobalOptionDefinition> CloneDefinitions(GlobalOptionConfigurationSnapshot state)
	{
		var definitions = new Dictionary<string, GlobalOptionDefinition>(StringComparer.OrdinalIgnoreCase);
		foreach (var pair in state.Definitions)
		{
			definitions.Add(pair.Key, pair.Value);
		}

		return definitions;
	}

	private static GlobalOptionConfigurationSnapshot CreateGlobalOptionConfigurationSnapshot(
		ReplCaseSensitivity caseSensitivity,
		Dictionary<string, GlobalOptionDefinition> definitions)
	{
		var ownership = new Dictionary<string, GlobalOptionDefinition>(ResolveOptionTokenComparer(caseSensitivity));
		foreach (var definition in definitions.Values)
		{
			ownership[definition.CanonicalToken] = definition;
			foreach (var alias in definition.Aliases)
			{
				ownership[alias] = definition;
			}
		}

		return new GlobalOptionConfigurationSnapshot(
			caseSensitivity,
			new ReadOnlyDictionary<string, GlobalOptionDefinition>(definitions),
			new ReadOnlyDictionary<string, GlobalOptionDefinition>(ownership));
	}

	internal sealed record GlobalOptionConfigurationSnapshot(
		ReplCaseSensitivity CaseSensitivity,
		IReadOnlyDictionary<string, GlobalOptionDefinition> Definitions,
		IReadOnlyDictionary<string, GlobalOptionDefinition> Ownership)
	{
		internal bool IsAliasHidden(GlobalOptionDefinition definition, string token) =>
			!string.Equals(token, definition.CanonicalToken, StringComparison.Ordinal)
			&& definition.HiddenAliases.Contains(
				token,
				CaseSensitivity == ReplCaseSensitivity.CaseInsensitive
					? StringComparer.OrdinalIgnoreCase
					: StringComparer.Ordinal);
	}

	private static string BuildDuplicateGlobalOptionMessage(string name, Type? existingOwner, Type? newOwner)
	{
		if (existingOwner is null && newOwner is null)
		{
			return $"A global option named '{name}' is already registered.";
		}

		var existingSource = existingOwner is null
			? "another registration"
			: $"typed global options '{existingOwner.Name}'";
		var newSource = newOwner is null
			? "this registration"
			: $"typed global options '{newOwner.Name}'";
		return $"A global option named '{name}' is already registered by {existingSource} and cannot also be registered by {newSource}.";
	}

	internal static string? FormatDefaultValue(object? value, Type type) =>
		value is not null && !IsDefaultForType(value, type)
			? FormatValue(value)
			: null;

	// A global option's default is text the accessor converts back into one value, which a collection cannot
	// round-trip through, so a collection records none.
	internal static string? FormatGlobalDefaultValue(object? value, Type type) =>
		IsCollectionValue(value) ? null : FormatDefaultValue(value, type);

	internal static bool IsCollectionValue(object? value) =>
		value is System.Collections.IEnumerable and not string;

	/// <summary>
	/// The text a default value is shown as: a collection's elements rather than its type name, and nothing
	/// for an empty collection.
	/// </summary>
	internal static string? FormatValue(object? value) =>
		value switch
		{
			null => null,
			string text => text,
			System.Collections.IEnumerable items => string.Join(", ", items.Cast<object?>()) is { Length: > 0 } joined
				? joined
				: null,
			_ => value.ToString(),
		};

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2067",
		Justification = "Activator.CreateInstance is only reached for value types, which always have a parameterless constructor.")]
	internal static bool IsDefaultForType(object value, Type type)
	{
		// The default of a Nullable<T> is null, never a value: a nullable property or
		// parameter initialized to 0 or false is a deliberate default that must be kept
		// (rendered in help, applied at resolution), unlike the implicit default of the
		// underlying non-nullable type.
		if (Nullable.GetUnderlyingType(type) is not null)
		{
			return false;
		}

		// Any non-nullable value type compares against its boxed CLR default (false, 0,
		// Guid.Empty, enum zero, DateTime.MinValue, ...), so implicit defaults captured
		// by `T? defaultValue = default` never become registration metadata. Reference
		// types have no implicit non-null default to suppress. Registration-time only,
		// so the boxing allocation is acceptable.
		return type.IsValueType && value.Equals(Activator.CreateInstance(type));
	}

	private static Type ResolveConstraintOrTypeName(
		string constraintOrTypeName,
		Dictionary<string, Func<string, bool>> customConstraints)
	{
		ArgumentNullException.ThrowIfNull(constraintOrTypeName);

		return constraintOrTypeName.ToLowerInvariant() switch
		{
			"string" or "alpha" or "email" => typeof(string),
			"int" => typeof(int),
			"long" => typeof(long),
			"bool" => typeof(bool),
			"guid" => typeof(Guid),
			"uri" or "url" or "urn" => typeof(Uri),
			"date" or "dateonly" or "date-only" => typeof(DateOnly),
			"datetime" or "date-time" => typeof(DateTime),
			"datetimeoffset" or "date-time-offset" => typeof(DateTimeOffset),
			"time" or "timeonly" or "time-only" => typeof(TimeOnly),
			"timespan" or "time-span" => typeof(TimeSpan),
			_ when customConstraints.ContainsKey(constraintOrTypeName) => typeof(string),
			_ => throw new ArgumentException(
				$"Unknown type or constraint name '{constraintOrTypeName}'. Use a known name (string, int, long, bool, guid, uri, date, datetime, timespan), a registered custom route constraint, or the generic AddGlobalOption<T> overload.",
				nameof(constraintOrTypeName)),
		};
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
