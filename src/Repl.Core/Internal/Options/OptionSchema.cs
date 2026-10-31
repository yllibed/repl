namespace Repl.Internal.Options;

internal sealed class OptionSchema
{
	public static OptionSchema Empty { get; } =
		new(
			[],
			new Dictionary<string, OptionSchemaParameter>(StringComparer.OrdinalIgnoreCase),
			ReplCaseSensitivity.CaseSensitive);

	public OptionSchema(
		IReadOnlyList<OptionSchemaEntry> entries,
		IReadOnlyDictionary<string, OptionSchemaParameter> parameters,
		ReplCaseSensitivity globalCaseSensitivity)
		: this(entries, parameters, () => globalCaseSensitivity)
	{
	}

	public OptionSchema(
		IReadOnlyList<OptionSchemaEntry> entries,
		IReadOnlyDictionary<string, OptionSchemaParameter> parameters,
		Func<ReplCaseSensitivity> resolveGlobalCaseSensitivity)
	{
		Entries = entries;
		Parameters = parameters;
		_resolveGlobalCaseSensitivity = resolveGlobalCaseSensitivity;
	}

	private readonly Func<ReplCaseSensitivity> _resolveGlobalCaseSensitivity;

	public IReadOnlyList<OptionSchemaEntry> Entries { get; }

	public IReadOnlyDictionary<string, OptionSchemaParameter> Parameters { get; }

	/// <summary>
	/// The options-group types whose properties this schema expanded into options, in handler order.
	/// </summary>
	/// <remarks>
	/// A parameter whose type is an options group but that is bound explicitly, from services or context, is not
	/// expanded, so discovery surfaces read the groups from here rather than from the handler's signature.
	/// </remarks>
	public IReadOnlyList<Type> OptionsGroupTypes { get; init; } = [];

	/// <summary>
	/// Whether the schema records <paramref name="parameter"/> itself as an option or argument, as opposed to a group
	/// property of the same name; <c>OptionSchemaBuilder.ShouldSkipSchemaParameter</c> decides which parameters those are.
	/// </summary>
	public bool IsOptionParameter(System.Reflection.ParameterInfo parameter) =>
		parameter.Name is { } name
		&& Parameters.TryGetValue(name, out var option)
		&& !option.IsGroupProperty;

	// Lazily materialized once: Entries is immutable and completion paths read this per
	// keystroke — recomputing the Distinct projection on every access was pure waste.
	// Benign race: concurrent first reads compute the same array.
	private string[]? _knownTokens;

	// Ordinal dedup: case-differing tokens can belong to DIFFERENT case-sensitive options
	// (per-entry overrides), so collapsing them ignoring case would drop a real token and
	// make "Did you mean" suggest the wrong casing.
	public IReadOnlyCollection<string> KnownTokens =>
		_knownTokens ??= [.. Entries.Select(entry => entry.Token).Distinct(StringComparer.Ordinal)];

	// Same lazy-materialization contract as KnownTokens: the schema is immutable, every
	// discovery surface reads these per keystroke, and a concurrent first read recomputes
	// the same result. A visibility change does not mutate a schema — it publishes a new
	// one (see WithParameter) — so these caches can never go stale.
	private OptionSchemaParameter[]? _discoverableParameters;
	private DiscoveryProjection? _sensitiveDiscovery;
	private DiscoveryProjection? _insensitiveDiscovery;

	/// <summary>
	/// Parameters that discovery surfaces may advertise: option-bearing and not hidden.
	/// This is the single projection help, documentation and completion consume, so a new
	/// surface cannot forget to filter.
	/// </summary>
	public IReadOnlyList<OptionSchemaParameter> DiscoverableParameters =>
		_discoverableParameters ??=
		[
			.. Parameters.Values.Where(parameter =>
				parameter.Mode != ReplParameterMode.ArgumentOnly && !parameter.IsHidden),
		];

	/// <summary>
	/// Entries whose owning parameter is discoverable. Entries outnumber parameters (aliases,
	/// value aliases, negated flags), so filtering here keeps every token of a hidden option
	/// out of completion, not just its canonical form.
	/// </summary>
	public IReadOnlyList<OptionSchemaEntry> DiscoverableEntries =>
		ResolveDiscoveryProjection().DiscoverableEntries;

	/// <summary>
	/// <see cref="KnownTokens"/> minus the tokens of hidden options.
	/// </summary>
	/// <remarks>
	/// Suggestions must read this, never <see cref="KnownTokens"/>: proposing "did you mean
	/// '--secret'?" turns a validation error into a way to enumerate hidden options by probing at
	/// small edit distance. Parsing keeps the full set, so a hidden option still binds when supplied.
	/// </remarks>
	public IReadOnlyCollection<string> DiscoverableTokens =>
		ResolveDiscoveryProjection().DiscoverableTokens;

	/// <summary>
	/// Whether the named parameter is hidden from discovery. Unknown names are not hidden:
	/// callers resolve tokens that may belong to route segments rather than options.
	/// </summary>
	public bool IsOptionHidden(string parameterName) =>
		Parameters.TryGetValue(parameterName, out var parameter) && parameter.IsHidden;

	/// <summary>
	/// Whether the named parameter is hidden from programmatic surfaces only. Independent of
	/// <see cref="IsOptionHidden"/>: an option can be withheld from agents while staying in help.
	/// </summary>
	public bool IsOptionAutomationHidden(string parameterName) =>
		Parameters.TryGetValue(parameterName, out var parameter) && parameter.IsAutomationHidden;

	/// <summary>
	/// Returns a schema with <paramref name="parameter"/> replacing its same-named entry.
	/// </summary>
	/// <remarks>
	/// The <see cref="Entries"/> list is reused verbatim, which is what makes a post-registration
	/// visibility change safe: tokens, arities and case sensitivity all live on the entries, so
	/// parsing and binding observe an identical schema. Only the derived discovery projections
	/// differ, and they are recomputed lazily on the new instance.
	/// </remarks>
	public OptionSchema WithParameter(OptionSchemaParameter parameter)
	{
		var parameters = new Dictionary<string, OptionSchemaParameter>(Parameters, StringComparer.OrdinalIgnoreCase)
		{
			[parameter.Name] = parameter,
		};

		return CopyWith(Entries, parameters);
	}

	// Every derived schema goes through here, so none loses what the builder recorded besides entries and parameters.
	private OptionSchema CopyWith(
		IReadOnlyList<OptionSchemaEntry> entries,
		IReadOnlyDictionary<string, OptionSchemaParameter> parameters) =>
		new(entries, parameters, _resolveGlobalCaseSensitivity)
		{
			OptionsGroupTypes = OptionsGroupTypes,
		};

	public OptionSchema WithAliasVisibility(
		string parameterName,
		string alias,
		bool isHidden,
		ReplCaseSensitivity? currentGlobalCaseSensitivity = null)
	{
		var canonicalEntry = FindNamedEntry(parameterName);
		var hasExactRegisteredAlias = Entries.Any(entry =>
			!ReferenceEquals(entry, canonicalEntry)
			&& string.Equals(entry.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(entry.Token, alias, StringComparison.Ordinal));
		if (!hasExactRegisteredAlias
			&& canonicalEntry is not null
			&& TokensAreEquivalent(canonicalEntry, alias, currentGlobalCaseSensitivity))
		{
			throw new ArgumentException(
				$"Token '{alias}' is the canonical token for option target '{parameterName}', not an alias.",
				nameof(alias));
		}

		// Explicit loop, not a Select with a captured `found` flag: a projection that relies on a
		// side effect only works because ToArray happens to enumerate eagerly, and breaks silently
		// if that materialization is ever removed.
		var found = false;
		var changed = false;
		var entries = new OptionSchemaEntry[Entries.Count];
		for (var i = 0; i < Entries.Count; i++)
		{
			var entry = Entries[i];
			if (!ReferenceEquals(entry, canonicalEntry)
				&& string.Equals(entry.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase)
				&& TokensAreEquivalent(entry, alias, currentGlobalCaseSensitivity))
			{
				found = true;
				// Accumulate with OR, not a plain assignment: multiple parser-equivalent aliases
				// (e.g. --ACCOUNT and --account under case-insensitive mode) can match the same
				// request. An earlier entry's real change must not be discarded just because a
				// later equivalent entry happens to already be in the requested state.
				var entryChanged = entry.IsHidden != isHidden;
				changed |= entryChanged;
				entries[i] = entryChanged ? entry with { IsHidden = isHidden } : entry;
			}
			else
			{
				entries[i] = entry;
			}
		}

		if (!found)
		{
			throw new KeyNotFoundException(
				$"No alias token '{alias}' is registered for option target '{parameterName}'.");
		}

		// A no-op call (the alias is already in the requested state) returns this same instance
		// rather than an equivalent copy, so the caller's CAS loop can recognize nothing changed
		// and skip publishing a schema and invalidating routing over it.
		return changed ? CopyWith(entries, Parameters) : this;
	}

	private bool TokensAreEquivalent(
		OptionSchemaEntry entry,
		string token,
		ReplCaseSensitivity? currentGlobalCaseSensitivity)
	{
		var effectiveCaseSensitivity = entry.CaseSensitivity
			?? currentGlobalCaseSensitivity
			?? _resolveGlobalCaseSensitivity();
		var comparison = effectiveCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		return string.Equals(entry.Token, token, comparison);
	}

	private DiscoveryProjection ResolveDiscoveryProjection() =>
		ResolveDiscoveryProjection(_resolveGlobalCaseSensitivity());

	private DiscoveryProjection ResolveDiscoveryProjection(ReplCaseSensitivity globalCaseSensitivity) =>
		globalCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? _insensitiveDiscovery ??= BuildDiscoveryProjection(globalCaseSensitivity)
			: _sensitiveDiscovery ??= BuildDiscoveryProjection(globalCaseSensitivity);

	private DiscoveryProjection BuildDiscoveryProjection(ReplCaseSensitivity globalCaseSensitivity)
	{
		var exactHidden = new HashSet<AliasVisibilityKey>(AliasVisibilityKeyComparer.Ordinal);
		var insensitiveHidden = new HashSet<AliasVisibilityKey>(AliasVisibilityKeyComparer.OrdinalIgnoreCase);
		foreach (var hidden in Entries.Where(static entry => entry.IsHidden))
		{
			var key = AliasVisibilityKey.From(hidden);
			var effectiveCaseSensitivity = hidden.CaseSensitivity ?? globalCaseSensitivity;
			_ = effectiveCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
				? insensitiveHidden.Add(key)
				: exactHidden.Add(key);
		}

		var canonicalEntries = ResolveNamedEntries().Values.ToHashSet();
		var visibleAliases = new HashSet<OptionSchemaEntry>();
		var visibleAliasesByParameter = new Dictionary<string, List<OptionSchemaEntry>>(StringComparer.OrdinalIgnoreCase);
		var discoverableEntries = new List<OptionSchemaEntry>(Entries.Count);
		foreach (var entry in Entries)
		{
			var key = AliasVisibilityKey.From(entry);
			var aliasIsVisible = !entry.IsHidden
				&& (canonicalEntries.Contains(entry)
					|| (!exactHidden.Contains(key) && !insensitiveHidden.Contains(key)));
			if (!aliasIsVisible)
			{
				continue;
			}

			visibleAliases.Add(entry);
			if (!visibleAliasesByParameter.TryGetValue(entry.ParameterName, out var parameterAliases))
			{
				parameterAliases = [];
				visibleAliasesByParameter.Add(entry.ParameterName, parameterAliases);
			}
			parameterAliases.Add(entry);
			if (!IsOptionHidden(entry.ParameterName))
			{
				discoverableEntries.Add(entry);
			}
		}

		var entries = discoverableEntries.ToArray();
		return new DiscoveryProjection(
			entries,
			[.. entries.Select(static entry => entry.Token).Distinct(StringComparer.Ordinal)],
			visibleAliases,
			entries.ToHashSet(),
			visibleAliasesByParameter.ToDictionary(
				static pair => pair.Key,
				static pair => (IReadOnlyList<OptionSchemaEntry>)pair.Value.ToArray(),
				StringComparer.OrdinalIgnoreCase));
	}

	internal IReadOnlyList<OptionSchemaEntry> ResolveDiscoverableEntries(
		ReplCaseSensitivity globalCaseSensitivity) =>
		ResolveDiscoveryProjection(globalCaseSensitivity).DiscoverableEntries;

	internal IReadOnlyCollection<string> ResolveDiscoverableTokens(
		ReplCaseSensitivity globalCaseSensitivity) =>
		ResolveDiscoveryProjection(globalCaseSensitivity).DiscoverableTokens;

	internal IReadOnlyList<OptionSchemaEntry> ResolveDiscoverableAliases(string parameterName) =>
		ResolveDiscoveryProjection().VisibleAliasesByParameter.GetValueOrDefault(parameterName) ?? [];

	internal IReadOnlyList<OptionSchemaEntry> ResolveDiscoverableAliases(
		string parameterName,
		ReplCaseSensitivity globalCaseSensitivity) =>
		ResolveDiscoveryProjection(globalCaseSensitivity).VisibleAliasesByParameter.GetValueOrDefault(parameterName) ?? [];

	internal bool IsEntryDiscoverable(OptionSchemaEntry entry) =>
		ResolveDiscoveryProjection().DiscoverableEntrySet.Contains(entry);

	internal bool IsEntryDiscoverableForTypedToken(
		OptionSchemaEntry entry,
		string typedToken,
		ReplCaseSensitivity globalCaseSensitivity)
	{
		// A canonical entry remains discoverable when a case-equivalent alias is hidden, but the
		// caller must not use that canonical fallback to reactivate the exact hidden spelling.
		// Preserve ordinary case-insensitive canonical input by vetoing only an exact registered
		// hidden token for the same semantic entry.
		var exactHiddenEquivalent = Entries.Any(candidate =>
			candidate.IsHidden
			&& string.Equals(candidate.Token, typedToken, StringComparison.Ordinal)
			&& string.Equals(candidate.ParameterName, entry.ParameterName, StringComparison.OrdinalIgnoreCase)
			&& candidate.TokenKind == entry.TokenKind
			&& string.Equals(candidate.InjectedValue, entry.InjectedValue, StringComparison.Ordinal));
		return !exactHiddenEquivalent
			&& ResolveDiscoveryProjection(globalCaseSensitivity).DiscoverableEntrySet.Contains(entry);
	}

	internal bool IsAliasDiscoverable(OptionSchemaEntry entry) =>
		ResolveDiscoveryProjection().VisibleAliasSet.Contains(entry);

	private sealed record DiscoveryProjection(
		OptionSchemaEntry[] DiscoverableEntries,
		string[] DiscoverableTokens,
		HashSet<OptionSchemaEntry> VisibleAliasSet,
		HashSet<OptionSchemaEntry> DiscoverableEntrySet,
		IReadOnlyDictionary<string, IReadOnlyList<OptionSchemaEntry>> VisibleAliasesByParameter);

	private readonly record struct AliasVisibilityKey(
		string ParameterName,
		OptionSchemaTokenKind TokenKind,
		string? InjectedValue,
		string Token)
	{
		public static AliasVisibilityKey From(OptionSchemaEntry entry) =>
			new(entry.ParameterName, entry.TokenKind, entry.InjectedValue, entry.Token);
	}

	private sealed class AliasVisibilityKeyComparer(StringComparer tokenComparer) : IEqualityComparer<AliasVisibilityKey>
	{
		public static AliasVisibilityKeyComparer Ordinal { get; } = new(StringComparer.Ordinal);
		public static AliasVisibilityKeyComparer OrdinalIgnoreCase { get; } = new(StringComparer.OrdinalIgnoreCase);

		public bool Equals(AliasVisibilityKey left, AliasVisibilityKey right) =>
			left.TokenKind == right.TokenKind
			&& string.Equals(left.ParameterName, right.ParameterName, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.InjectedValue, right.InjectedValue, StringComparison.Ordinal)
			&& tokenComparer.Equals(left.Token, right.Token);

		public int GetHashCode(AliasVisibilityKey value)
		{
			var hash = new HashCode();
			hash.Add(value.ParameterName, StringComparer.OrdinalIgnoreCase);
			hash.Add(value.TokenKind);
			hash.Add(value.InjectedValue, StringComparer.Ordinal);
			hash.Add(value.Token, tokenComparer);
			return hash.ToHashCode();
		}
	}

	public IReadOnlyList<OptionSchemaEntry> ResolveToken(string token, ReplCaseSensitivity globalCaseSensitivity)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return [];
		}

		var matches = new List<OptionSchemaEntry>();
		foreach (var entry in Entries)
		{
			var effectiveCase = entry.CaseSensitivity ?? globalCaseSensitivity;
			var comparison = effectiveCase == ReplCaseSensitivity.CaseInsensitive
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			if (string.Equals(entry.Token, token, comparison))
			{
				matches.Add(entry);
			}
		}

		return matches;
	}

	public bool TryGetParameter(string parameterName, out OptionSchemaParameter parameter) =>
		Parameters.TryGetValue(parameterName, out parameter!);

	/// <summary>
	/// Effective arity of a parameter, resolved from its named-option/flag entry;
	/// parameters without such an entry default to the permissive <see cref="ReplArity.ZeroOrMore"/>.
	/// </summary>
	public ReplArity ResolveParameterArity(string parameterName) =>
		FindNamedEntry(parameterName)?.Arity ?? ReplArity.ZeroOrMore;

	/// <summary>
	/// Canonical display token of a parameter's named-option/flag entry (with prefix),
	/// or null for parameters without one (e.g. ArgumentOnly).
	/// </summary>
	public string? ResolveDisplayToken(string parameterName) =>
		FindNamedEntry(parameterName)?.Token;

	private Dictionary<string, OptionSchemaEntry>? _namedEntries;

	private OptionSchemaEntry? FindNamedEntry(string parameterName) =>
		ResolveNamedEntries().GetValueOrDefault(parameterName);

	private Dictionary<string, OptionSchemaEntry> ResolveNamedEntries()
	{
		if (_namedEntries is { } cached)
		{
			return cached;
		}

		var entries = new Dictionary<string, OptionSchemaEntry>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in Entries)
		{
			if (entry.TokenKind is OptionSchemaTokenKind.NamedOption or OptionSchemaTokenKind.BoolFlag)
			{
				entries.TryAdd(entry.ParameterName, entry);
			}
		}

		return _namedEntries = entries;
	}
}
