using Repl.Internal.Options;

namespace Repl;

/// <summary>
/// Provides shell (bash/zsh/fish/etc.) completion candidates for the Repl routing graph.
/// </summary>
internal sealed class ShellCompletionEngine(CoreReplApp app)
{

	public async ValueTask<string[]> ResolveShellCompletionCandidatesAsync(
		string line,
		int cursor,
		ShellKind shell,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		// Completion must not poison the durable routing cache (see the interactive path).
		var activeGraph = app.ResolveActiveRoutingGraph(useDurableCache: false);
		var state = AnalyzeShellCompletionInput(line, cursor);
		if (state.PriorTokens.Length == 0)
		{
			return [];
		}

		var resolution = ResolveShellCommitted(state.PriorTokens, activeGraph);
		return await CollectShellCandidatesAsync(state, resolution, activeGraph, shell, serviceProvider, cancellationToken)
			.ConfigureAwait(false);
	}

	private async ValueTask<string[]> CollectShellCandidatesAsync(
		ShellCompletionInputState state,
		ShellResolution resolution,
		ActiveRoutingGraph activeGraph,
		ShellKind shell,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var commandPrefix = resolution.CommandPrefix;
		var optionsTerminated = resolution.OptionsTerminated;
		var routeMatch = resolution.Match;
		var currentTokenPrefix = state.CurrentTokenPrefix;
		// Same gate as the interactive menu: single-dash prefixes surface short option
		// aliases (-f); signed numeric literals stay positional. After the POSIX "--"
		// separator no option names may be offered — everything is positional.
		var currentTokenIsOption = !optionsTerminated && AutocompleteEngine.IsOptionPrefixToken(currentTokenPrefix);
		// Terminal-for-options only when every positional segment (required or optional) is
		// filled — an unfilled trailing segment would capture the accepted option/value.
		var hasTerminalRoute = routeMatch is not null
			&& commandPrefix.Length == routeMatch.Route.Template.Segments.Count;
		var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var candidates = new List<string>(capacity: 16);

		// The pending option is whatever the LAST committed token is (the first prior token is
		// the executable name). Enum-value completion only applies when that pending option is
		// the route's own trailing option — not a valued global that the resolution stripped
		// (e.g. "run --mode --tenant " is pending on --tenant, not --mode).
		var afterExecutable = state.PriorTokens.Length > 1 ? state.PriorTokens[1..] : [];
		var routeOptionIsLastCommitted = routeMatch is not null
			&& routeMatch.RemainingTokens.Count > 0
			&& afterExecutable.Length > 0
			&& string.Equals(afterExecutable[^1], routeMatch.RemainingTokens[^1], StringComparison.Ordinal);
		// No !currentTokenIsOption gate here: a dash-prefixed CURRENT token can still be the
		// pending option's value in transit (the provider offers '-42' while '-' is typed);
		// the consumability filter drops anything the parser would read as the next option.
		if (!optionsTerminated
			&& hasTerminalRoute
			&& routeOptionIsLastCommitted
			&& await TryAddPendingOptionValueCandidatesAsync(
					routeMatch!, currentTokenPrefix, shell, serviceProvider, dedupe, candidates, cancellationToken)
				.ConfigureAwait(false))
		{
			candidates.Sort(StringComparer.OrdinalIgnoreCase);
			return [.. candidates];
		}

		// A valued option still awaiting its value (and not completed by the block above)
		// makes the current token that value — offering a command or option name here would
		// misparse.
		if (app.Autocomplete.IsPendingOptionValue(afterExecutable, routeMatch, optionsTerminated, currentTokenPrefix))
		{
			return [];
		}

		await AddShellPositionalProviderCandidatesAsync(
				resolution, activeGraph, currentTokenPrefix, shell, serviceProvider, dedupe, candidates, cancellationToken)
			.ConfigureAwait(false);
		AddShellCommandAndOptionCandidates(
			resolution, activeGraph, currentTokenPrefix, currentTokenIsOption, hasTerminalRoute, dedupe, candidates);
		candidates.Sort(StringComparer.OrdinalIgnoreCase);
		return [.. candidates];
	}

	// The pending option's own value source — an opted-in provider first, enum member names
	// as the fallback — mirroring the interactive menu's precedence.
	private async ValueTask<bool> TryAddPendingOptionValueCandidatesAsync(
		RouteMatch match,
		string currentTokenPrefix,
		ShellKind shell,
		IServiceProvider serviceProvider,
		HashSet<string> dedupe,
		List<string> candidates,
		CancellationToken cancellationToken) =>
		await TryAddPendingOptionProviderCandidatesAsync(
				match, currentTokenPrefix, shell, serviceProvider, candidates, cancellationToken)
			.ConfigureAwait(false)
		|| TryAddRouteEnumValueCandidates(match, currentTokenPrefix, dedupe, candidates);

	// Invokes the value provider targeting the positional segment the current token occupies,
	// mirroring the interactive menu (shared target resolution, same option-region and
	// option-prefix exclusions). Only providers registered with
	// CompletionProviderScope.InteractiveAndShell run here: the bridge spawns a process per
	// completion request and blocks the user's shell, so slow providers are opt-in.
	private async ValueTask AddShellPositionalProviderCandidatesAsync(
		ShellResolution resolution,
		ActiveRoutingGraph activeGraph,
		string currentTokenPrefix,
		ShellKind shell,
		IServiceProvider serviceProvider,
		HashSet<string> dedupe,
		List<string> candidates,
		CancellationToken cancellationToken)
	{
		// A dash-prefixed token stays eligible: routing binds it to an unfilled positional
		// ('deploy -prod' → target == "-prod") for the bare '-', a partial '-pr', or a full
		// '-prod'. Target resolution + the per-candidate constraint check are the filters;
		// option NAMES keep their own menu. Once the terminal route carries trailing option
		// tokens, no positional remains open, so nothing is offered here.
		if (resolution.Match is { RemainingTokens.Count: > 0 })
		{
			return;
		}

		// A completion requested from inside an already-open shell quote (e.g. bash 'app
		// contact "Ne') is unsafe for provider values: the shell keeps the opening quote, so
		// our emitted token lands INSIDE it as literal characters and any interpolation the
		// outer quote performs still runs on acceptance. We cannot reshape the user's opening
		// quote from here, so provider values are dropped in that context (static command and
		// option names, which are controlled identifiers, are unaffected).
		if (HasUnterminatedQuote(currentTokenPrefix))
		{
			return;
		}

		var matchingRoutes = app.Autocomplete.CollectVisibleMatchingRoutes(
			resolution.CommandPrefix,
			StringComparison.OrdinalIgnoreCase,
			activeGraph.Routes,
			activeGraph.Contexts);
		var targets = AutocompleteEngine.ResolvePositionalCompletionTargets(
			matchingRoutes,
			resolution.CommandPrefix,
			StringComparison.OrdinalIgnoreCase,
			app.OptionsSnapshot.Parsing);
		var valuePrefix = AutocompleteEngine.DecodeTokenPrefix(currentTokenPrefix);
		// Provider VALUES dedupe case-sensitively (a positional binds verbatim at execution,
		// so "Prod"/"prod" are distinct); the shared case-insensitive set still marks the
		// value so an identical command literal is not offered twice.
		var valueDedupe = new HashSet<string>(StringComparer.Ordinal);
		foreach (var target in targets)
		{
			if (target.Route.Command.IsCompletionShellScoped(target.Segment.Name))
			{
				await EmitShellProviderValuesAsync(
						resolution.CommandPrefix, activeGraph, target, valuePrefix, shell,
						serviceProvider, valueDedupe, dedupe, candidates, cancellationToken)
					.ConfigureAwait(false);
			}
		}
	}

	private async ValueTask EmitShellProviderValuesAsync(
		string[] commandPrefix,
		ActiveRoutingGraph activeGraph,
		(RouteDefinition Route, DynamicRouteSegment Segment, CompletionDelegate Provider) target,
		string valuePrefix,
		ShellKind shell,
		IServiceProvider serviceProvider,
		HashSet<string> valueDedupe,
		HashSet<string> dedupe,
		List<string> candidates,
		CancellationToken cancellationToken)
	{
		var provided = await InvokeProviderWithDeadlineAsync(target.Provider, serviceProvider, valuePrefix, cancellationToken)
			.ConfigureAwait(false);
		var parsing = app.OptionsSnapshot.Parsing;
		foreach (var value in provided ?? [])
		{
			// Parity per candidate: the segment constraint must accept it AND execution must
			// route it to THIS segment (a higher-scoring or hidden literal would shadow it, a
			// global-option value would be stripped — CandidateBindsToProviderRoute resolves
			// against the full active graph after global parsing); values are then encoded as
			// literal data in the TARGET shell's syntax (see QuoteValueForShell) or dropped
			// when unrepresentable.
			if (!string.IsNullOrWhiteSpace(value)
				&& IsShellSafeCandidate(value)
				&& RouteConstraintEvaluator.IsMatch(target.Segment, value, parsing)
				&& app.Autocomplete.CandidateBindsToProviderRoute(commandPrefix, value, target.Route, activeGraph)
				&& QuoteValueForShell(value, shell) is { } insertion
				&& valueDedupe.Add(insertion))
			{
				candidates.Add(insertion);
				dedupe.Add(insertion);
			}
		}
	}

	// The bridge protocol is line-delimited plain text: an embedded CR/LF forges an extra
	// completion record, and terminal control characters (C0, DEL, C1 — including the ESC
	// and OSC introducers) would reach the user's completion UI unfiltered. Provider values
	// reflect external data (filenames, database labels), so an unsafe candidate is rejected
	// WHOLE — the protocol has no escaping that could represent it. Both range scans are
	// SIMD-accelerated (MemoryExtensions.ContainsAnyInRange).
	private static bool IsShellSafeCandidate(string value) =>
		AutocompleteEngine.IsControlFreeValue(value);

	// Characters that never need shell quoting — a conservative ASCII identifier-ish set.
	// Anything else (whitespace, $, `, quotes, globs, redirects, non-ASCII, ...) routes the
	// value through the single-quote literal form below. Over-quoting is always safe.
	private static readonly System.Buffers.SearchValues<char> s_shellPlainChars =
		System.Buffers.SearchValues.Create("+,-./0123456789:=@ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz");

	// PowerShell treats '@' (splatting) and ',' (array operator) as syntax even in a bare
	// argument — '@x' splats and 'a,b' becomes a two-element array — so they are NOT plain
	// data there and force the single-quote literal form. Otherwise identical to the set above.
	private static readonly System.Buffers.SearchValues<char> s_powerShellPlainChars =
		System.Buffers.SearchValues.Create("+-./0123456789:=ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz");

	// True when the raw current-token prefix ends inside an unclosed quote (the tokenizer's
	// quote state machine: a quote opens, the matching quote closes; the opening quote is
	// part of the prefix because token spans start before it). Used to detect an open-quoted
	// completion context the bridge cannot safely reshape.
	internal static bool HasUnterminatedQuote(string prefix)
	{
		char? quote = null;
		foreach (var ch in prefix)
		{
			if (quote is { } active)
			{
				if (ch == active)
				{
					quote = null;
				}
			}
			else if (ch is '"' or '\'')
			{
				quote = ch;
			}
		}

		return quote is not null;
	}

	// Encodes one provider VALUE as LITERAL data for the target shell. The emitted candidate is
	// inserted into the user's command line and re-parsed by that shell, so an interpolating
	// form is a command-execution boundary for provider-reflected data — in POSIX shells
	// "$(...)" runs the substitution on acceptance. A single-quoted literal is verbatim in
	// every target shell (only the plain-char fast path skips the quotes). It must ALSO survive
	// the bridge's own tokenizer on the NEXT Tab: a value containing an apostrophe or backslash
	// forces a shell-specific escape ('\'' , '' , \' , \\ ) that TokenizeInputSpans cannot
	// re-lex, so such values are rejected (null) rather than emitted in a form that would break
	// subsequent completion. Deliberately SEPARATE from the interactive tokenizer quoting, which
	// can use the other quote kind because the reader re-tokenizes with the same rules.
	internal static string? QuoteValueForShell(string value, ShellKind shell)
	{
		var plainChars = shell == ShellKind.PowerShell ? s_powerShellPlainChars : s_shellPlainChars;
		if (!value.AsSpan().ContainsAnyExcept(plainChars))
		{
			return value;
		}

		// An apostrophe can't live in a single-quoted literal without a shell-specific escape
		// ('\'' , '' ) that the bridge tokenizer can't re-lex on the next Tab, so drop it in
		// every shell. A backslash is ordinary literal data inside single quotes for bash, zsh,
		// PowerShell and nu (and round-trips through the bridge tokenizer), so 'C:\Temp'-style
		// values complete there — but fish's single quotes DO escape '\', so it's dropped only
		// for fish.
		if (value.Contains('\'', StringComparison.Ordinal)
			|| (shell == ShellKind.Fish && value.Contains('\\', StringComparison.Ordinal)))
		{
			return null;
		}

		return "'" + value + "'";
	}

	// Bounds one provider invocation to ShellCompletion.ProviderTimeout and isolates its
	// faults. The invoking shell blocks on the bridge until it answers, so a stalled provider
	// is ABANDONED at the deadline via WaitAsync — a plain await could never return when the
	// provider ignores its cancellation token — and the invocation itself runs on the pool
	// (Task.Run) so SYNCHRONOUS provider work (sync I/O, Thread.Sleep) cannot stall the
	// bridge before the returned task even exists. Caller cancellation propagates as usual.
	// Returns null on timeout OR provider fault so the caller degrades to static candidates
	// (a transient lookup failure must not fail the completion invocation or spam the shell's
	// completion stream).
	private async ValueTask<IReadOnlyList<string>?> InvokeProviderWithDeadlineAsync(
		CompletionDelegate provider,
		IServiceProvider serviceProvider,
		string input,
		CancellationToken cancellationToken)
	{
		var timeout = app.OptionsSnapshot.ShellCompletion.ProviderTimeout;
		var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var providerTask = Task.Run(
			() => provider(new CompletionContext(serviceProvider), input, deadline.Token).AsTask(),
			CancellationToken.None);
		try
		{
			var result = await providerTask.WaitAsync(timeout, TimeProvider.System, cancellationToken).ConfigureAwait(false);
			deadline.Dispose();
			return result;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			deadline.Dispose();
			throw;
		}
		catch (TimeoutException)
		{
			// The provider overran the deadline. Cancel the token to let a cooperative
			// provider unwind, but off a DETACHED task: Cancel() runs the provider's
			// registered cancellation callbacks inline, and a slow/blocking callback must not
			// keep the bridge (and the user's shell) waiting past the deadline. The same
			// detached task observes the abandoned provider's eventual fault and disposes the
			// CTS once it settles, so Cancel() and Dispose() never race.
			AbandonProvider(deadline, providerTask);
			return null;
		}
		catch (Exception)
		{
			// Provider fault (sync or async) — degrade to static candidates.
			deadline.Dispose();
			return null;
		}
	}

	private static void AbandonProvider(CancellationTokenSource deadline, Task providerTask) =>
		_ = Task.Run(async () =>
		{
			try
			{
				// Awaiting here is safe — this runs off the bridge thread, so a blocking
				// provider cancellation callback delays only this detached task.
				await deadline.CancelAsync().ConfigureAwait(false);
			}
			catch
			{
				// A provider-registered cancellation callback threw — irrelevant to the bridge.
			}

			try
			{
				// VSTHRD003: awaiting a task started elsewhere is intentional — there is no
				// sync context on this pool task, so no deadlock; we only observe the fault.
#pragma warning disable VSTHRD003
				await providerTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
			catch
			{
				// Observe the abandoned provider's fault so it does not escalate.
			}

			deadline.Dispose();
		});

	// Runs the pending route option's value provider when it opted into the shell bridge.
	// Returns true when the provider ran (its answer is final, even when empty), so an enum
	// fallback never overrides an explicit provider — the interactive menu's precedence.
	private async ValueTask<bool> TryAddPendingOptionProviderCandidatesAsync(
		RouteMatch match,
		string currentTokenPrefix,
		ShellKind shell,
		IServiceProvider serviceProvider,
		List<string> candidates,
		CancellationToken cancellationToken)
	{
		// An open-quoted value context is unsafe for provider output (see the positional
		// path); skipping here lets the static enum fallback still run.
		if (HasUnterminatedQuote(currentTokenPrefix)
			|| !TryResolvePendingRouteOption(match, out var entry)
			|| !match.Route.Command.Completions.TryGetValue(entry.ParameterName, out var completion)
			|| !match.Route.Command.IsCompletionShellScoped(entry.ParameterName))
		{
			return false;
		}

		var provided = await InvokeProviderWithDeadlineAsync(
				completion, serviceProvider, AutocompleteEngine.DecodeTokenPrefix(currentTokenPrefix), cancellationToken)
			.ConfigureAwait(false);
		if (provided is null)
		{
			// Timed out: no final answer from the provider — fall back to static candidates.
			return false;
		}

		// Option VALUES dedupe case-sensitively: a string option value is case-significant at
		// execution, so provider results differing only by case must both survive (parity with
		// the interactive pending path). The invocation parser consumes a following token as
		// the option's value only when it is not option-like, so non-consumable candidates
		// (accepting one would leave the option unset) are dropped by the parser's own rule.
		var valueDedupe = new HashSet<string>(StringComparer.Ordinal);
		foreach (var value in provided)
		{
			// Consumability is judged on the SEMANTIC value (the parser sees the decoded
			// token); the emitted candidate is pre-quoted when it needs quoting.
			if (!string.IsNullOrWhiteSpace(value)
				&& IsShellSafeCandidate(value)
				&& InvocationOptionParser.ShouldConsumeFollowingTokenAsValue(value)
				&& QuoteValueForShell(value, shell) is { } insertion
				&& valueDedupe.Add(insertion))
			{
				candidates.Add(insertion);
			}
		}

		return true;
	}

	private void AddShellCommandAndOptionCandidates(
		ShellResolution resolution,
		ActiveRoutingGraph activeGraph,
		string currentTokenPrefix,
		bool currentTokenIsOption,
		bool hasTerminalRoute,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		// No subcommand can follow once a terminal route already carries trailing option
		// tokens (see the interactive path).
		if (!currentTokenIsOption && resolution.Match is not { RemainingTokens.Count: > 0 })
		{
			AddShellCommandCandidates(
				resolution.CommandPrefix,
				currentTokenPrefix,
				activeGraph.Routes,
				activeGraph.Contexts,
				dedupe,
				candidates);
		}

		if (!resolution.OptionsTerminated
			&& (currentTokenIsOption || (string.IsNullOrEmpty(currentTokenPrefix) && hasTerminalRoute)))
		{
			AddShellOptionCandidates(
				hasTerminalRoute ? resolution.Match!.Route : null,
				currentTokenPrefix,
				candidates);
		}
	}

	// Mirrors the interactive engine's single resolution: the first prior token is the
	// executable name; global options are stripped with the arity-aware parser, unique
	// command prefixes are expanded (so "i" resolves to "install" like execution does), and
	// the route is resolved on the remaining tokens BEFORE option parsing — so dash tokens
	// and the bare "--" bind to segments as positional values. The match's trailing tokens
	// are the route's option region; a "--" among them terminates options, one bound to a
	// segment does not.
	private ShellResolution ResolveShellCommitted(string[] priorTokens, ActiveRoutingGraph activeGraph)
	{
		if (priorTokens.Length <= 1)
		{
			return new ShellResolution([], Match: null, OptionsTerminated: false);
		}

		var afterExecutable = new ArraySegment<string>(priorTokens, offset: 1, count: priorTokens.Length - 1);
		var stripped = GlobalOptionParser
			.Parse(afterExecutable, app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing)
			.RemainingTokens;
		var expanded = ExpandUniquePrefixes(stripped as string[] ?? [.. stripped], activeGraph);
		if (app.Resolve(expanded, activeGraph.Routes) is { } match)
		{
			var segmentCount = Math.Min(match.Route.Template.Segments.Count, expanded.Length);
			var optionsTerminated = false;
			foreach (var trailing in match.RemainingTokens)
			{
				if (string.Equals(trailing, "--", StringComparison.Ordinal))
				{
					optionsTerminated = true;
					break;
				}
			}

			return new ShellResolution(expanded[..segmentCount], match, optionsTerminated);
		}

		return new ShellResolution(expanded, Match: null, OptionsTerminated: false);
	}

	// Bounded unique-prefix/alias expansion mirroring the interactive engine.
	private string[] ExpandUniquePrefixes(string[] tokens, ActiveRoutingGraph activeGraph)
	{
		if (tokens.Length == 0)
		{
			return tokens;
		}

		var expansionDepth = 0;
		foreach (var route in activeGraph.Routes)
		{
			expansionDepth = Math.Max(expansionDepth, route.Template.Segments.Count);
		}

		foreach (var context in activeGraph.Contexts)
		{
			expansionDepth = Math.Max(expansionDepth, context.Template.Segments.Count);
		}

		if (tokens.Length <= expansionDepth)
		{
			var resolution = app.ResolveUniquePrefixes(tokens, activeGraph);
			return resolution.IsAmbiguous ? tokens : resolution.Tokens;
		}

		var headResolution = app.ResolveUniquePrefixes(tokens[..expansionDepth], activeGraph);
		return headResolution.IsAmbiguous
			? tokens
			: [.. headResolution.Tokens, .. tokens[expansionDepth..]];
	}

	private bool TryAddRouteEnumValueCandidates(
		RouteMatch match,
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		if (!TryResolvePendingRouteOption(match, out var entry))
		{
			return false;
		}

		if (!match.Route.OptionSchema.TryGetParameter(entry.ParameterName, out var parameter))
		{
			return false;
		}

		var enumType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
		if (!enumType.IsEnum)
		{
			return false;
		}

		var effectiveCaseSensitivity = parameter.CaseSensitivity ?? app.OptionsSnapshot.Parsing.OptionCaseSensitivity;
		var comparison = effectiveCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		var beforeCount = candidates.Count;
		foreach (var enumName in Enum
			         .GetNames(enumType)
			         .Where(name => name.StartsWith(currentTokenPrefix, comparison)))
		{
			TryAddShellCompletionCandidate(enumName, dedupe, candidates);
		}

		return candidates.Count > beforeCount;
	}

	private bool TryResolvePendingRouteOption(
		RouteMatch match,
		out OptionSchemaEntry entry)
	{
		entry = default!;

		// The pending option is the LAST token in the route's trailing option region — not a
		// dash-prefixed token that routing already bound to a positional segment. Deriving it
		// from match.RemainingTokens (rather than the raw prior tokens) is what keeps
		// "deploy -m" (where -m fills {target}) from being mistaken for a pending "-m" option.
		if (match.RemainingTokens.Count == 0)
		{
			return false;
		}

		var previousToken = match.RemainingTokens[^1];
		// A single dash is enough: short option aliases (e.g. "-m") take values too, and the
		// schema resolves them like any other token below.
		if (!AutocompleteEngine.IsOptionPrefixToken(previousToken))
		{
			return false;
		}

		var separatorIndex = previousToken.IndexOfAny(['=', ':']);
		if (separatorIndex >= 0)
		{
			return false;
		}

		var matches = match.Route.OptionSchema.ResolveToken(previousToken, app.OptionsSnapshot.Parsing.OptionCaseSensitivity);
		var distinct = matches
			.DistinctBy(candidate => (candidate.ParameterName, candidate.TokenKind, candidate.InjectedValue), ShellOptionSchemaEntryComparer.Instance)
			.ToArray();
		if (distinct.Length != 1)
		{
			return false;
		}

		if (distinct[0].TokenKind is not (OptionSchemaTokenKind.NamedOption or OptionSchemaTokenKind.BoolFlag))
		{
			return false;
		}

		entry = distinct[0];
		return true;
	}

	private static void TryAddShellCompletionCandidate(
		string candidate,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		if (string.IsNullOrWhiteSpace(candidate) || !dedupe.Add(candidate))
		{
			return;
		}

		candidates.Add(candidate);
	}

	private void AddShellCommandCandidates(
		string[] commandPrefix,
		string currentTokenPrefix,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		var matchingRoutes = app.Autocomplete.CollectVisibleMatchingRoutes(
			commandPrefix,
			StringComparison.OrdinalIgnoreCase,
			routes,
			contexts);
		foreach (var route in matchingRoutes)
		{
			if (commandPrefix.Length >= route.Template.Segments.Count
				|| route.Template.Segments[commandPrefix.Length] is not LiteralRouteSegment literal
				|| !literal.Value.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			TryAddShellCompletionCandidate(literal.Value, dedupe, candidates);
		}
	}

	private void AddShellOptionCandidates(
		RouteDefinition? route,
		string currentTokenPrefix,
		List<string> candidates)
	{
		// Option candidates dedupe with the PARSER's case semantics, not the command/UI
		// OrdinalIgnoreCase set: under case-sensitive option parsing, "-m" and "-M" can bind
		// to different parameters and both are executable, so they must not collapse. (Option
		// tokens start with '-' and never collide with command names, so a separate set is safe.)
		var optionDedupe = new HashSet<string>(
			app.OptionsSnapshot.Parsing.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
				? StringComparer.OrdinalIgnoreCase
				: StringComparer.Ordinal);
		AddGlobalShellOptionCandidates(currentTokenPrefix, optionDedupe, candidates);

		if (route is null)
		{
			return;
		}

		AddRouteShellOptionCandidates(route, currentTokenPrefix, optionDedupe, candidates);
	}

	private void AddGlobalShellOptionCandidates(
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		var options = app.OptionsSnapshot;
		OptionTokenCompletionSource.CollectGlobalOptionTokens(
			options,
			currentTokenPrefix,
			options.Parsing.OptionCaseSensitivity.ToStringComparison(),
			dedupe,
			candidates);
	}

	private void AddRouteShellOptionCandidates(
		RouteDefinition route,
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		OptionTokenCompletionSource.CollectRouteOptionTokens(
			route,
			currentTokenPrefix,
			app.OptionsSnapshot.Parsing.OptionCaseSensitivity,
			dedupe,
			candidates);
	}

	internal static ShellCompletionInputState AnalyzeShellCompletionInput(string input, int cursor)
	{
		input ??= string.Empty;
		cursor = Math.Clamp(cursor, 0, input.Length);
		var tokens = AutocompleteEngine.TokenizeInputSpans(input);
		for (var i = 0; i < tokens.Count; i++)
		{
			var token = tokens[i];
			if (cursor < token.Start || cursor > token.End)
			{
				continue;
			}

			var prior = new string[i];
			for (var priorIndex = 0; priorIndex < i; priorIndex++)
			{
				prior[priorIndex] = tokens[priorIndex].Value;
			}

			var prefix = input[token.Start..cursor];
			return new ShellCompletionInputState(prior, prefix);
		}

		var trailingPriorCount = 0;
		foreach (var token in tokens)
		{
			if (token.End <= cursor)
			{
				trailingPriorCount++;
			}
		}

		if (trailingPriorCount == 0)
		{
			return new ShellCompletionInputState([], CurrentTokenPrefix: string.Empty);
		}

		var trailingPrior = new string[trailingPriorCount];
		var index = 0;
		foreach (var token in tokens)
		{
			if (token.End <= cursor)
			{
				trailingPrior[index++] = token.Value;
			}
		}

		return new ShellCompletionInputState(trailingPrior, CurrentTokenPrefix: string.Empty);
	}

	internal readonly record struct ShellCompletionInputState(
		string[] PriorTokens,
		string CurrentTokenPrefix);

	private readonly record struct ShellResolution(
		string[] CommandPrefix,
		RouteMatch? Match,
		bool OptionsTerminated);

	internal static string ResolveShellCompletionCommandName(
		IReadOnlyList<string>? commandLineArgs,
		string? processPath,
		string? fallbackName)
	{
		if (commandLineArgs is { Count: > 0 })
		{
			var commandHead = TryGetCommandHead(commandLineArgs[0]);
			if (!string.IsNullOrWhiteSpace(commandHead))
			{
				return commandHead;
			}
		}

		var processHead = TryGetCommandHead(processPath);
		if (!string.IsNullOrWhiteSpace(processHead))
		{
			return processHead;
		}

		return string.IsNullOrWhiteSpace(fallbackName) ? "repl" : fallbackName;
	}

	private static string? TryGetCommandHead(string? pathLike)
	{
		if (string.IsNullOrWhiteSpace(pathLike))
		{
			return null;
		}

		var fileName = Path.GetFileName(pathLike.Trim());
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return null;
		}

		foreach (var extension in KnownExecutableExtensions)
		{
			if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
			{
				var head = fileName[..^extension.Length];
				return string.IsNullOrWhiteSpace(head) ? null : head;
			}
		}

		return fileName;
	}

	private static readonly string[] KnownExecutableExtensions =
	[
		".exe",
		".cmd",
		".bat",
		".com",
		".ps1",
		".dll",
	];

	public string ResolveShellCompletionCommandName()
	{
		var docApp = app.BuildDocumentationApp();
		return ResolveShellCompletionCommandName(
			Environment.GetCommandLineArgs(),
			Environment.ProcessPath,
			docApp.Name);
	}

	private sealed class ShellOptionSchemaEntryComparer : IEqualityComparer<(string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue)>
	{
		public static ShellOptionSchemaEntryComparer Instance { get; } = new();

		public bool Equals(
			(string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue) x,
			(string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue) y) =>
			string.Equals(x.ParameterName, y.ParameterName, StringComparison.OrdinalIgnoreCase)
			&& x.TokenKind == y.TokenKind
			&& string.Equals(x.InjectedValue, y.InjectedValue, StringComparison.Ordinal);

		public int GetHashCode((string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue) obj)
		{
			var parameterHash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ParameterName);
			var injectedHash = obj.InjectedValue is null
				? 0
				: StringComparer.Ordinal.GetHashCode(obj.InjectedValue);
			return HashCode.Combine(parameterHash, (int)obj.TokenKind, injectedHash);
		}
	}
}
