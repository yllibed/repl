namespace Repl.Tests;

/// <summary>
/// Regression tests for issue #45: WithCompletion value providers must fire while the
/// parameter value is being typed (and only there), on the interactive autocomplete path.
/// </summary>
[TestClass]
public sealed class Given_InteractiveAutocomplete_ValueProviderCandidates
{
	[TestMethod]
	[Description("Issue #45: a WithCompletion provider fires WHILE the parameter value is being typed — 'contact inspect ab' offers the provider's candidates for the partial value 'ab', the same way command tokens are completed.")]
	public async Task When_TypingPositionalValue_Then_ProviderCandidatesAreOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact inspect {clientId}", static string (string clientId) => clientId)
			.WithCompletion("clientId", static (_, input, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>([$"{input}001", $"{input}002"]))
			.WithDescription("Inspect a contact.");

		var result = await ResolveAutocompleteAsync(sut, "contact inspect ab").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("ab001", because: "the provider must be invoked with the partial value being typed");
		values.Should().Contain("ab002");
	}

	[TestMethod]
	[Description("Issue #45: the provider also fires at the EMPTY value position — 'contact inspect ' (cursor after the space) invokes the provider with an empty input so it can list all candidates.")]
	public async Task When_ValuePositionIsEmpty_Then_ProviderCandidatesAreOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact inspect {clientId}", static string (string clientId) => clientId)
			.WithCompletion("clientId", static (_, input, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>([$"{input}001", $"{input}002"]))
			.WithDescription("Inspect a contact.");

		var result = await ResolveAutocompleteAsync(sut, "contact inspect ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("001", because: "the provider must be invoked with an empty input at the value position");
	}

	[TestMethod]
	[Description("Issue #45: a command with SEVERAL providers resolves the one targeting the segment at the typed position — typing the first value of 'copy {source} {destination}' offers source's candidates only, never destination's.")]
	public async Task When_TypingFirstOfTwoValues_Then_OnlyItsProviderCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Map("copy {source} {destination}", static string (string source, string destination) => source + destination)
			.WithCompletion("source", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["ga-src"]))
			.WithCompletion("destination", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["bu-dst"]))
			.WithDescription("Copy.");

		var result = await ResolveAutocompleteAsync(sut, "copy g").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("ga-src", because: "the {source} segment is at the typed position");
		values.Should().NotContain("bu-dst", because: "destination's provider targets a later segment");
	}

	[TestMethod]
	[Description("Issue #45: with the first value committed, typing the SECOND value of 'copy {source} {destination}' offers destination's candidates only — the provider is resolved by the segment name at the current position, not by being the route's sole registration.")]
	public async Task When_TypingSecondOfTwoValues_Then_OnlyItsProviderCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Map("copy {source} {destination}", static string (string source, string destination) => source + destination)
			.WithCompletion("source", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["ga-src"]))
			.WithCompletion("destination", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["bu-dst"]))
			.WithDescription("Copy.");

		var result = await ResolveAutocompleteAsync(sut, "copy ga-src b").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("bu-dst", because: "the {destination} segment is at the typed position");
		values.Should().NotContain("ga-src", because: "source's segment is already bound");
	}

	[TestMethod]
	[Description("Issue #45 regression: once the value is committed and the cursor sits on the NEXT token ('deploy x '), the provider must NOT fire — its candidates would land on a position that can no longer bind to {target}, so accepting one would add a positional that execution rejects.")]
	public async Task When_ValueIsCommittedAndCursorIsOnNextToken_Then_ProviderDoesNotFire()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion("target", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["zo-profile"]))
			.WithDescription("Deploy.");

		var result = await ResolveAutocompleteAsync(sut, "deploy x ").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("zo-profile",
			because: "{target} is already bound to 'x'; a suggestion here cannot bind to the parameter");
	}

	[TestMethod]
	[Description("A signed numeric literal is a positional value, not an option prefix: typing 'deploy x -4' on 'deploy {target} {count?:int}' invokes {count}'s provider with '-4' — the option-name guard must not swallow numeric values (mirrors the invocation parser's rule).")]
	public async Task When_TypingSignedNumericValue_Then_ProviderStillRuns()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target} {count?:int}", static string (string target, int? count) => $"{target}:{count}")
			.WithCompletion("count", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["-42"]))
			.WithDescription("Deploy.");

		var result = await ResolveAutocompleteAsync(sut, "deploy x -4").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("-42",
			because: "'-4' is a signed numeric literal, so it is a positional value for the open {count} segment");
	}

	[TestMethod]
	[Description("With overloaded dynamic routes, every route's provider participates and each CANDIDATE is checked against its own segment constraint: for 'show {id:int}' + 'show {name}' with prefix-filtering providers, 'show a' offers only the string route's match — nothing from the int route can start with 'a'.")]
	public async Task When_TypedValueViolatesOverloadConstraint_Then_OnlyViableProviderCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Map("show {id:int}", static string (int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.WithCompletion("id", static (_, input, _) => ValueTask.FromResult<IReadOnlyList<string>>(
				[.. s_intIds.Where(id => id.StartsWith(input, StringComparison.Ordinal))]))
			.WithDescription("Show by id.");
		sut.Map("show {name}", static string (string name) => name)
			.WithCompletion("name", static (_, input, _) => ValueTask.FromResult<IReadOnlyList<string>>(
				[.. s_names.Where(name => name.StartsWith(input, StringComparison.Ordinal))]))
			.WithDescription("Show by name.");

		var result = await ResolveAutocompleteAsync(sut, "show a").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("alice", because: "the {name} overload can still complete 'a'");
		values.Should().NotContain("42").And.NotContain("77");
	}

	[TestMethod]
	[Description("When the typed value could still select SEVERAL overloads, each provider answers for its own route: 'show 4' offers the int route's '42' — a partial token does not lock completion to one overload.")]
	public async Task When_TypedValueSatisfiesSeveralOverloads_Then_ViableProvidersAreMerged()
	{
		var sut = CoreReplApp.Create();
		sut.Map("show {id:int}", static string (int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.WithCompletion("id", static (_, input, _) => ValueTask.FromResult<IReadOnlyList<string>>(
				[.. s_intIds.Where(id => id.StartsWith(input, StringComparison.Ordinal))]))
			.WithDescription("Show by id.");
		sut.Map("show {name}", static string (string name) => name)
			.WithCompletion("name", static (_, input, _) => ValueTask.FromResult<IReadOnlyList<string>>(
				[.. s_names.Where(name => name.StartsWith(input, StringComparison.Ordinal))]))
			.WithDescription("Show by name.");

		var result = await ResolveAutocompleteAsync(sut, "show 4").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("42",
			because: "'4' still satisfies the int overload, whose provider stays active");
	}

	[TestMethod]
	[Description("A PARTIAL typed value must not silence a constrained route's provider: on 'lookup {id:guid}', 'lookup 550e' is not yet a complete Guid, but the provider is still invoked and its complete-Guid candidates are offered — accepting one replaces the partial token with a valid value.")]
	public async Task When_TypingPartialConstrainedValue_Then_ProviderStillCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Map("lookup {id:guid}", static string (Guid id) => id.ToString())
			.WithCompletion("id", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(
				["550e8400-e29b-41d4-a716-446655440000"]))
			.WithDescription("Lookup.");

		var result = await ResolveAutocompleteAsync(sut, "lookup 550e").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("550e8400-e29b-41d4-a716-446655440000",
			because: "the partial prefix will become the accepted complete value; it must not gate the provider");
	}

	[TestMethod]
	[Description("Suggestion/execution parity is enforced on the CANDIDATE, not the prefix: a provider value that violates its segment's constraint ('not-a-number' for {id:int}) is never offered, because accepting it could not bind at execution.")]
	public async Task When_ProviderReturnsCandidateViolatingConstraint_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("show {id:int}", static string (int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.WithCompletion("id", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(
				["42", "not-a-number"]))
			.WithDescription("Show by id.");

		var result = await ResolveAutocompleteAsync(sut, "show 4").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("42");
		values.Should().NotContain("not-a-number", because: "a candidate the segment constraint rejects can never bind at execution");
	}

	[TestMethod]
	[Description("Shell parity for overload viability by candidate: 'app show a' offers the string route's shell-scoped match only — the int route's provider has nothing starting with 'a', and its candidates would be constraint-checked anyway.")]
	public async Task When_TypedValueViolatesOverloadConstraint_Then_ShellOffersOnlyViableProvider()
	{
		var sut = CoreReplApp.Create();
		sut.Map("show {id:int}", static string (int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.WithCompletion(
				"id",
				static (_, input, _) => ValueTask.FromResult<IReadOnlyList<string>>(
					[.. s_intIds.Where(id => id.StartsWith(input, StringComparison.Ordinal))]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Show by id.");
		sut.Map("show {name}", static string (string name) => name)
			.WithCompletion(
				"name",
				static (_, input, _) => ValueTask.FromResult<IReadOnlyList<string>>(
					[.. s_names.Where(name => name.StartsWith(input, StringComparison.Ordinal))]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Show by name.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app show a").ConfigureAwait(false);

		candidates.Should().Contain("alice");
		candidates.Should().NotContain("42").And.NotContain("77");
	}

	[TestMethod]
	[Description("Positional provider values dedupe case-sensitively on the interactive menu: a string positional is bound verbatim at execution, so 'Prod' and 'prod' are distinct values and must both be offered — the UI's case-insensitive comparer must not collapse them.")]
	public async Task When_PositionalProviderReturnsCaseDistinctValues_Then_BothAreOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("case {name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["Prod", "prod"]))
			.WithDescription("Case.");

		var result = await ResolveAutocompleteAsync(sut, "case p").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("Prod").And.Contain("prod",
			because: "positional values are case-significant at execution, so both spellings must survive");
	}

	[TestMethod]
	[Description("Shell parity: a shell-scoped positional provider returning 'Prod' and 'prod' offers both through the bridge — provider VALUES use an ordinal dedupe, separate from the case-insensitive command-name set, like the pending-option path.")]
	public async Task When_PositionalProviderReturnsCaseDistinctValues_Then_ShellOffersBoth()
	{
		var sut = CoreReplApp.Create();
		sut.Map("case {name}", static string (string name) => name)
			.WithCompletion(
				"name",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["Prod", "prod"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Case.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app case p").ConfigureAwait(false);

		candidates.Should().Contain("Prod").And.Contain("prod",
			because: "positional values are case-significant at execution, so both spellings must survive");
	}

	[TestMethod]
	[Description("A provider registered with CompletionProviderScope.InteractiveAndShell is invoked by the shell completion bridge while the value is being typed: 'app contact inspect ab' offers the provider's candidates, matching the interactive menu.")]
	public async Task When_ShellScopedProviderAndValueIsTyped_Then_ShellOffersProviderCandidates()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact inspect {clientId}", static string (string clientId) => clientId)
			.WithCompletion(
				"clientId",
				static (_, input, _) => ValueTask.FromResult<IReadOnlyList<string>>([$"{input}001", $"{input}002"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Inspect a contact.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app contact inspect ab").ConfigureAwait(false);

		candidates.Should().Contain("ab001", because: "the shell-scoped provider must run for the value being typed");
		candidates.Should().Contain("ab002");
	}

	[TestMethod]
	[Description("A provider with the default scope (Interactive) is NOT invoked by the shell bridge — each shell Tab spawns the process and blocks on it, so slow providers must be excluded unless the author opted in.")]
	public async Task When_DefaultScopedProviderAndValueIsTyped_Then_ShellOffersNothing()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact inspect {clientId}", static string (string clientId) => clientId)
			.WithCompletion("clientId", static (_, input, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>([$"{input}001", $"{input}002"]))
			.WithDescription("Inspect a contact.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app contact inspect ab").ConfigureAwait(false);

		candidates.Should().NotContain("ab001", because: "shell invocation is opt-in per provider");
	}

	[TestMethod]
	[Description("A pending valued route option completes through its shell-scoped provider on the shell bridge ('app run --channel ' offers the provider's values), dropping candidates the invocation parser would not consume as the option's separate value: dash-prefixed values are the next option, signed numeric literals bind as values.")]
	public async Task When_ShellScopedProviderOnPendingOption_Then_ShellOffersConsumableValues()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none")
			.WithCompletion(
				"channel",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["alpha", "--prod", "-42"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app run --channel ").ConfigureAwait(false);

		candidates.Should().Contain("alpha", because: "the pending option's shell-scoped provider must run");
		candidates.Should().Contain("-42", because: "a signed numeric literal is consumable as the option's separate value");
		candidates.Should().NotContain("--prod", because: "a dash-prefixed candidate parses as the next option, leaving --channel unset");
	}

	[TestMethod]
	[Description("A pending valued route option whose provider has the default scope offers nothing on the shell bridge — the opt-in rule applies to option-value providers exactly like positional ones.")]
	public async Task When_DefaultScopedProviderOnPendingOption_Then_ShellOffersNothing()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none")
			.WithCompletion("channel", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["alpha"]))
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app run --channel ").ConfigureAwait(false);

		candidates.Should().NotContain("alpha", because: "shell invocation is opt-in per provider");
	}

	[TestMethod]
	[Description("A dash-prefixed transitional value after a pending route option still runs the shell-scoped provider: 'app run --channel -' offers '-42' (the parser consumes a signed numeric as the option's value) — the option-name suppression must not swallow the pending option's own values.")]
	public async Task When_DashValueIsTypedForPendingOption_Then_ShellProviderStillRuns()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] int? count) => count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")
			.WithCompletion(
				"count",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["-42"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app run --count -").ConfigureAwait(false);

		candidates.Should().Contain("-42",
			because: "the pending option's provider must run for the transitional dash prefix, like the interactive path");
	}

	[TestMethod]
	[Description("Interactive parity pin for the same scenario: 'run --count -' invokes the pending option's provider and offers the consumable '-42' while the dash prefix is being typed.")]
	public async Task When_DashValueIsTypedForPendingOption_Then_InteractiveProviderStillRuns()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] int? count) => count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")
			.WithCompletion("count", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["-42"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --count -").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("-42",
			because: "the pending option's provider runs for the transitional dash prefix");
	}

	[TestMethod]
	[Description("Shell parity with the interactive no-misfire rule: once the value is committed ('app deploy x '), even a shell-scoped provider must not fire — its candidates could no longer bind to the parameter at execution.")]
	public async Task When_ShellScopedProviderAndValueIsCommitted_Then_ShellOffersNothing()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["zo-profile"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app deploy x ").ConfigureAwait(false);

		candidates.Should().NotContain("zo-profile", because: "{target} is already bound; nothing typed here can bind to it");
	}

	[TestMethod]
	[Description("A provider VALUE containing whitespace is emitted pre-quoted ('New York' → '\"New York\"') so accepting the suggestion round-trips through tokenization as ONE argument instead of splitting into 'New' and 'York'.")]
	public async Task When_ProviderReturnsValueWithSpaces_Then_SuggestionIsQuoted()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact {name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["New York"]))
			.WithDescription("Contact.");

		var result = await ResolveAutocompleteAsync(sut, "contact Ne").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("\"New York\"",
			because: "the inserted text must tokenize back to the single value the provider offered");
	}

	[TestMethod]
	[Description("The provider receives the DECODED semantic prefix, not the raw lexical slice: for 'contact \"Ne' the provider is invoked with 'Ne' so a normal prefix lookup can match 'New York'.")]
	public async Task When_TypingQuotedPrefix_Then_ProviderReceivesDecodedPrefix()
	{
		string? capturedInput = null;
		var sut = CoreReplApp.Create();
		sut.Map("contact {name}", static string (string name) => name)
			.WithCompletion("name", (_, input, _) =>
			{
				capturedInput = input;
				return ValueTask.FromResult<IReadOnlyList<string>>(["New York"]);
			})
			.WithDescription("Contact.");

		await ResolveAutocompleteAsync(sut, "contact \"Ne").ConfigureAwait(false);

		capturedInput.Should().Be("Ne", because: "the quote is lexical syntax, not part of the value prefix");
	}

	[TestMethod]
	[Description("A provider value containing BOTH quote kinds cannot be represented by the tokenizer (which has no escape sequences), so it is dropped rather than offered as a suggestion that could never round-trip.")]
	public async Task When_ProviderValueContainsBothQuoteKinds_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact {name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>(["ga\"bu'zo"]))
			.WithDescription("Contact.");

		var result = await ResolveAutocompleteAsync(sut, "contact g").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain(static v => v.Contains("ga", StringComparison.Ordinal),
			because: "an unrepresentable value must not produce a suggestion that breaks on acceptance");
	}

	[TestMethod]
	[Description("A pending option's provider value with whitespace is pre-quoted too: 'run --channel ' offering 'New York' emits '\"New York\"' so the accepted option value stays one token.")]
	public async Task When_PendingProviderReturnsValueWithSpaces_Then_SuggestionIsQuoted()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none")
			.WithCompletion("channel", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["New York"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --channel ").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("\"New York\"");
	}

	[TestMethod]
	[Description("Shell quoting for an unquoted prefix: 'app contact Ne' emits a value with spaces single-quoted as literal SHELL data ('New York' → 'New York' in single quotes) — never a double-quoted form bash would interpolate. (An OPEN-quoted prefix is handled separately: provider values are dropped.)")]
	public async Task When_ShellValueHasSpaces_Then_ItIsSingleQuoted()
	{
		string? capturedInput = null;
		var sut = CoreReplApp.Create();
		sut.Map("contact {name}", static string (string name) => name)
			.WithCompletion(
				"name",
				(_, input, _) =>
				{
					capturedInput = input;
					return ValueTask.FromResult<IReadOnlyList<string>>(["New York"]);
				},
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Contact.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app contact Ne").ConfigureAwait(false);

		capturedInput.Should().Be("Ne");
		candidates.Should().Contain("'New York'");
	}

	[TestMethod]
	[Description("A shell provider value containing an apostrophe is DROPPED, not escaped: the shell-specific escape ('\\'' , '' , \\' ) is not re-lexable by the bridge's own tokenizer on the next Tab, so emitting it would break subsequent completion. Values without an apostrophe still complete.")]
	public async Task When_ShellValueContainsApostrophe_Then_ItIsDropped()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact {name}", static string (string name) => name)
			.WithCompletion(
				"name",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["O'Brien Co", "Acme Inc"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Contact.");
		var shellEngine = new ShellCompletionEngine(sut);

		var bash = await ResolveShellCandidatesAsync(shellEngine, "app contact A").ConfigureAwait(false);
		var pwsh = await ResolveShellCandidatesAsync(shellEngine, "app contact O", ShellKind.PowerShell).ConfigureAwait(false);

		bash.Should().Contain("'Acme Inc'", because: "an apostrophe-free value with a space round-trips as a single-quoted literal");
		pwsh.Should().NotContain(static c => c.Contains("Brien", StringComparison.Ordinal),
			because: "an apostrophe value cannot be re-lexed by the bridge on the next Tab, so it is dropped");
	}

	[TestMethod]
	[Description("The apostrophe drop is shell-only: the interactive menu still offers O'Brien (quoted with the OTHER quote kind, which the reader's own tokenizer round-trips).")]
	public async Task When_InteractiveValueContainsApostrophe_Then_ItIsStillOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact {name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["O'Brien Co"]))
			.WithDescription("Contact.");

		var result = await ResolveAutocompleteAsync(sut, "contact O").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("\"O'Brien Co\"",
			because: "the interactive path double-quotes an apostrophe value, which its own tokenizer round-trips");
	}

	[TestMethod]
	[Description("The transitional bare '-' keeps signed-value providers eligible: on 'deploy {count:int}', typing 'deploy -' offers the provider's '-42' — the completed value binds and executes as the positional integer — while non-numeric candidates stay out of the option-name menu.")]
	public async Task When_TypingBareDashForSignedPositional_Then_ProviderStillCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {count:int}", static string (int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.WithCompletion("count", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["-42"]))
			.WithDescription("Deploy.");

		var result = await ResolveAutocompleteAsync(sut, "deploy -").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("-42",
			because: "'-' is the first character of a signed value the provider can complete");
	}

	[TestMethod]
	[Description("Shell parity for the transitional bare '-': 'app deploy -' offers the shell-scoped provider's '-42' on 'deploy {count:int}', matching what execution binds.")]
	public async Task When_TypingBareDashForSignedPositional_Then_ShellProviderStillCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {count:int}", static string (int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.WithCompletion(
				"count",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["-42"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app deploy -").ConfigureAwait(false);

		candidates.Should().Contain("-42");
	}

	[TestMethod]
	[Description("An interactive positional provider that THROWS must not abort completion: its suggestions are dropped and the resolve still returns (the exception would otherwise escape through ReadLineAsync and kill the session).")]
	public async Task When_InteractivePositionalProviderThrows_Then_ResolveDegradesGracefully()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion("target", static (_, _, _) =>
				throw new InvalidOperationException("probe"))
			.WithDescription("Deploy.");

		var act = async () => await ResolveAutocompleteAsync(sut, "deploy z").ConfigureAwait(false);

		var result = await act.Should().NotThrowAsync().ConfigureAwait(false);
		result.Which.Suggestions.Select(static s => s.Value).Should().NotContain("probe");
	}

	[TestMethod]
	[Description("An interactive pending-option provider that returns a FAULTED task must not abort completion either: the fault is isolated and the resolve returns without the provider's values.")]
	public async Task When_InteractivePendingProviderThrows_Then_ResolveDegradesGracefully()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none")
			.WithCompletion("channel", static async (_, _, _) =>
			{
				await Task.Yield();
				throw new InvalidOperationException("probe");
			})
			.WithDescription("Run.");

		var act = async () => await ResolveAutocompleteAsync(sut, "run --channel ").ConfigureAwait(false);

		await act.Should().NotThrowAsync().ConfigureAwait(false);
	}

	[TestMethod]
	[Description("A dash-prefixed NON-numeric value is a valid positional bind (routing binds before option parsing, so target == '-prod'): on 'deploy {target}' the provider's '-prod' is offered at 'deploy -', not just signed numerics — eligibility is whether the segment constraint accepts the candidate.")]
	public async Task When_TypingBareDashForStringPositional_Then_ProviderValueIsOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion("target", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["-prod", "-staging"]))
			.WithDescription("Deploy.");

		var result = await ResolveAutocompleteAsync(sut, "deploy -").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("-prod",
			because: "'-prod' binds the string {target} positionally, so its provider value is valid at 'deploy -'");
	}

	[TestMethod]
	[Description("The dash-prefixed eligibility extends beyond the first character: typing 'deploy -pr' still invokes the provider (routing binds '-prod' to the string {target}), so completion does not vanish once the user types past the bare '-'.")]
	public async Task When_TypingPartialDashPrefixedValue_Then_ProviderStillCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion("target", static (_, input, _) => ValueTask.FromResult<IReadOnlyList<string>>(
				[.. s_dashTargets.Where(v => v.StartsWith(input, StringComparison.Ordinal))]))
			.WithDescription("Deploy.");

		var result = await ResolveAutocompleteAsync(sut, "deploy -pr").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("-prod",
			because: "a dash-prefixed positional stays provider-eligible past the first character");
	}

	[TestMethod]
	[Description("Shell parity for a partial dash-prefixed value: 'app deploy -pr' offers '-prod'.")]
	public async Task When_TypingPartialDashPrefixedValue_Then_ShellProviderStillCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, input, _) => ValueTask.FromResult<IReadOnlyList<string>>(
					[.. s_dashTargets.Where(v => v.StartsWith(input, StringComparison.Ordinal))]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app deploy -pr").ConfigureAwait(false);

		candidates.Should().Contain("-prod");
	}

	[TestMethod]
	[Description("Shell parity: 'app deploy -' on 'deploy {target}' offers the shell-scoped provider's dash-prefixed string value '-prod'.")]
	public async Task When_TypingBareDashForStringPositional_Then_ShellProviderValueIsOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["-prod", "-staging"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app deploy -").ConfigureAwait(false);

		candidates.Should().Contain("-prod");
	}

	[TestMethod]
	[Description("Shell completion requested from inside an open quote drops provider values: on 'app contact \"Ne' (open double quote) the shell keeps the opening quote, so any emitted token would be interpolated in place — provider candidates are withheld rather than returned unsafely.")]
	public async Task When_ShellPrefixHasOpenQuote_Then_ProviderCandidatesAreDropped()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact {name}", static string (string name) => name)
			.WithCompletion(
				"name",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["New York", "$(printf PWNED)"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Contact.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app contact \"Ne").ConfigureAwait(false);

		candidates.Should().NotContain(static c => c.Contains("PWNED", StringComparison.Ordinal),
			because: "an open-quoted context cannot be safely reshaped, so provider values are withheld");
		candidates.Should().NotContain(static c => c.Contains("New York", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("The shell quote-context guard suppresses provider values for ANY quoted current token, not just a naively-open one: a shell-ESCAPED delimiter (bash \"a\\\", PowerShell 'a'') or a balanced-looking pair keeps the target shell inside an interpolating quote, which a delimiter-counting scan would miss. PrefixHasQuoteContext returns true whenever a quote character is present, and false for plain prefixes.")]
	[DataRow("\"a\\\"", DisplayName = "escaped double-quote delimiter (bash)")]
	[DataRow("'a''", DisplayName = "doubled single-quote (PowerShell)")]
	[DataRow("\"ab\"", DisplayName = "balanced-looking closed pair")]
	[DataRow("\"Ne", DisplayName = "open double quote")]
	public void When_PrefixCarriesAnyQuote_Then_GuardSuppresses(string prefix)
	{
		ShellCompletionEngine.PrefixHasQuoteContext(prefix).Should().BeTrue(
			because: "a quoted current token cannot be reshaped without the shell's escaping rules");
	}

	[TestMethod]
	[Description("The quote-context guard does not over-fire on ordinary unquoted prefixes: a plain value being typed still gets provider completion.")]
	[DataRow("Ne")]
	[DataRow("C:/tmp")]
	[DataRow("-prod")]
	[DataRow("")]
	public void When_PrefixHasNoQuote_Then_GuardAllows(string prefix)
	{
		ShellCompletionEngine.PrefixHasQuoteContext(prefix).Should().BeFalse();
	}

	[TestMethod]
	[Description("PowerShell treats '@' and ',' as syntax even bare, so those provider values are single-quoted as literal data: 'app deploy ' offers '@payload' and 'alpha,beta' each wrapped in single quotes, not emitted raw where pwsh would splat/list them.")]
	public async Task When_PowerShellValueContainsMetacharacters_Then_ItIsSingleQuoted()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["@payload", "alpha,beta"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app deploy ", ShellKind.PowerShell).ConfigureAwait(false);

		candidates.Should().Contain("'@payload'").And.Contain("'alpha,beta'");
		candidates.Should().NotContain("@payload").And.NotContain("alpha,beta");
	}

	[TestMethod]
	[Description("A provider value that execution would route to a DIFFERENT (higher-scoring literal) route is not offered by the provider path: for 'pick {name}' (provider returns 'status') alongside a literal 'pick status', accepting 'status' runs the literal — the value never binds to {name} — and the literal is already a command candidate, so the provider must not also offer it.")]
	public async Task When_ProviderValueRoutesToLiteral_Then_ProviderDoesNotOfferIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("pick status", static string () => "literal").WithDescription("Pick status.");
		sut.Map("pick {name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["status", "alice"]))
			.WithDescription("Pick by name.");

		var result = await ResolveAutocompleteAsync(sut, "pick s").ConfigureAwait(false);

		var parameterValues = result.Suggestions
			.Where(static s => s.Kind == ConsoleLineReader.AutocompleteSuggestionKind.Parameter)
			.Select(static s => s.Value)
			.ToArray();
		parameterValues.Should().NotContain("status",
			because: "'pick status' routes to the literal, so the value never binds to {name}");
	}

	[TestMethod]
	[Description("Ownership is vetted against the FULL active graph, not the discovery-filtered set: a HIDDEN literal 'pick status' shadows the provider's 'status' at execution, so the value must not be offered (and a hidden command must not leak indirectly through completion).")]
	public async Task When_ProviderValueRoutesToHiddenLiteral_Then_ProviderDoesNotOfferIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("pick status", static string () => "literal").WithDescription("Pick status.").Hidden();
		sut.Map("pick {name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["status", "alice"]))
			.WithDescription("Pick by name.");

		var result = await ResolveAutocompleteAsync(sut, "pick s").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("status",
			because: "a hidden literal still wins at execution, so the value never binds to {name}");
	}

	[TestMethod]
	[Description("Shell parity: a provider value shadowed by a hidden literal is dropped on the bridge too.")]
	public async Task When_ProviderValueRoutesToHiddenLiteral_Then_ShellDoesNotOfferIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("pick status", static string () => "literal").WithDescription("Pick status.").Hidden();
		sut.Map("pick {name}", static string (string name) => name)
			.WithCompletion(
				"name",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["status", "alice"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Pick by name.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app pick s").ConfigureAwait(false);

		candidates.Should().NotContain("status");
	}

	[TestMethod]
	[Description("Ownership vetting expands unique command prefixes like execution: a provider value 'sta' that uniquely prefixes a literal sibling 'pick status' expands to that literal at execution, so it must not be offered as a {name} value.")]
	public async Task When_ProviderValueUniquelyPrefixesLiteral_Then_ProviderDoesNotOfferIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("pick status", static string () => "literal").WithDescription("Pick status.");
		sut.Map("pick {name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["sta", "alice"]))
			.WithDescription("Pick by name.");

		var result = await ResolveAutocompleteAsync(sut, "pick s").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("sta",
			because: "'sta' expands to the literal 'status' before routing, so it never binds to {name}");
	}

	[TestMethod]
	[Description("Ownership vetting judges incomplete routes via the missing-argument winner: with 'pick {name} {id}' and a higher-scoring literal 'pick status {id}', the value 'status' would resolve to the literal once {id} is typed, so it must not be offered for {name} even though 'pick status' has no terminal match yet.")]
	public async Task When_ProviderValueShadowedByIncompleteLiteral_Then_ProviderDoesNotOfferIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("pick status {id}", static string (string id) => id).WithDescription("Pick status by id.");
		sut.Map("pick {name} {id}", static string (string name, string id) => name + id)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["status", "alice"]))
			.WithDescription("Pick by name and id.");

		var result = await ResolveAutocompleteAsync(sut, "pick s").ConfigureAwait(false);

		// 'status' is legitimately offered as a COMMAND literal (heading toward 'pick status
		// {id}'); the provider must not additionally contribute it as a {name} VALUE.
		var providerValues = result.Suggestions
			.Where(static s => s.Kind == ConsoleLineReader.AutocompleteSuggestionKind.Parameter)
			.Select(static s => s.Value)
			.ToArray();
		providerValues.Should().NotContain("status",
			because: "the higher-scoring incomplete literal 'pick status {id}' shadows the value at execution");
	}

	[TestMethod]
	[Description("A still-incomplete provider route with no colliding sibling is NOT over-rejected: 'copy {source} {destination}' offers a source value even though the route needs {destination} too (the missing-argument winner is the provider's own route).")]
	public async Task When_IncompleteProviderRouteHasNoRival_Then_ValueIsOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("copy {source} {destination}", static string (string source, string destination) => source + destination)
			.WithCompletion("source", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["ga-src"]))
			.WithDescription("Copy.");

		var result = await ResolveAutocompleteAsync(sut, "copy g").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("ga-src",
			because: "the only incomplete-route winner is the provider's own route");
	}

	[TestMethod]
	[Description("A backslash provider value ('C:\\Temp') completes on bash — it is literal data inside a single-quoted candidate and round-trips through the bridge tokenizer — but is dropped on fish, whose single quotes escape backslashes.")]
	public async Task When_ShellValueContainsBackslash_Then_OfferedExceptOnFish()
	{
		var sut = CoreReplApp.Create();
		sut.Map("open {path}", static string (string path) => path)
			.WithCompletion(
				"path",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["C:\\Temp"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Open.");
		var shellEngine = new ShellCompletionEngine(sut);

		var bash = await ResolveShellCandidatesAsync(shellEngine, "app open C").ConfigureAwait(false);
		var fish = await ResolveShellCandidatesAsync(shellEngine, "app open C", ShellKind.Fish).ConfigureAwait(false);

		bash.Should().Contain("'C:\\Temp'", because: "a backslash is literal inside a bash single-quoted value");
		fish.Should().NotContain(static c => c.Contains("Temp", StringComparison.Ordinal),
			because: "fish single quotes escape backslashes, so the value cannot round-trip");
	}

	[TestMethod]
	[Description("The route-shadowing filter does not over-reject: a provider value that has no colliding literal ('alice') still binds to {name} and is offered.")]
	public async Task When_ProviderValueHasNoLiteralCollision_Then_ItIsOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("pick status", static string () => "literal").WithDescription("Pick status.");
		sut.Map("pick {name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["status", "alice"]))
			.WithDescription("Pick by name.");

		var result = await ResolveAutocompleteAsync(sut, "pick a").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("alice",
			because: "'alice' binds to {name}; only literal-shadowed values are dropped");
	}

	[TestMethod]
	[Description("fish and nushell insert the completion line as a VALUE and quote it themselves, so the bridge must not pre-quote: a value needing quoting (a space) is dropped for fish/nu (whereas bash single-quotes it), while a plain value still completes.")]
	[DataRow(ShellKind.Fish)]
	[DataRow(ShellKind.Nu)]
	public async Task When_FishOrNuValueNeedsQuoting_Then_ItIsDropped(ShellKind shell)
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["New York", "plainval"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app deploy ", shell).ConfigureAwait(false);

		candidates.Should().Contain("plainval", because: "a plain value is inserted verbatim by fish/nu");
		candidates.Should().NotContain(static c => c.Contains("New York", StringComparison.Ordinal),
			because: "fish/nu quote the value themselves; a pre-quoted or spaced value can't round-trip");
	}

	[TestMethod]
	[Description("fish/nu quote completion values themselves, so the bash/zsh bare-insertion concerns ('=' → zsh EQUALS, globbing, non-ASCII) do not apply: a value that is a single token with no whitespace, no quote, and no shell metacharacter ('env=prod', 'café') is offered VERBATIM — it round-trips through the bridge tokenizer as one bare token — where the shared plain set (tuned for bare bash/zsh) needlessly dropped it. A spaced value is still dropped (fish/nu escape the space, breaking round-trip).")]
	[DataRow(ShellKind.Fish)]
	[DataRow(ShellKind.Nu)]
	public async Task When_FishOrNuValueIsBareSafe_Then_ItIsOffered(ShellKind shell)
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["env=prod", "café", "New York"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app deploy ", shell).ConfigureAwait(false);

		candidates.Should().Contain("env=prod", because: "'=' is not a bare-insertion hazard for fish/nu and round-trips as one token");
		candidates.Should().Contain("café", because: "a non-ASCII letter is ordinary data for fish/nu and the bridge tokenizer never splits on it");
		candidates.Should().NotContain(static c => c.Contains("New York", StringComparison.Ordinal),
			because: "a spaced value is escaped by fish/nu and cannot round-trip through the whitespace-splitting bridge tokenizer");
	}

	[TestMethod]
	[Description("The fish/nu bare-safe relaxation stays SAFE: a value carrying a shell metacharacter fish/nu would escape ('$(printf PWNED)') still cannot round-trip (the escaped form re-lexes differently) and is dropped, so no interpolating token is emitted.")]
	[DataRow(ShellKind.Fish)]
	[DataRow(ShellKind.Nu)]
	public async Task When_FishOrNuValueHasMetacharacter_Then_ItIsStillDropped(ShellKind shell)
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["$(printf PWNED)", "safeval"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app deploy ", shell).ConfigureAwait(false);

		candidates.Should().Contain("safeval");
		candidates.Should().NotContain(static c => c.Contains("PWNED", StringComparison.Ordinal),
			because: "a command-substitution value cannot be represented as a bare fish/nu token that round-trips");
	}

	[TestMethod]
	[Description("A bool route option is not a pending value position: 'app run --force ' must fall through to normal option/command completion rather than entering the provider/enum pending path (which would suppress it). The bool option's own provider values are not offered as if awaiting a value.")]
	public async Task When_PendingTokenIsBoolFlag_Then_NotTreatedAsPendingValue()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] bool force, [ReplOption] string? channel) => force ? "on" : (channel ?? "off"))
			.WithCompletion(
				"force",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["bogus-bool-value"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app run --force ").ConfigureAwait(false);

		candidates.Should().NotContain("bogus-bool-value", because: "a bool flag takes no value, so its provider must not run as pending");
		candidates.Should().Contain("--channel", because: "normal option completion must still follow a set bool flag");
	}

	[TestMethod]
	[Description("A pending option's provider value that cannot convert to the option parameter's type is filtered, matching the positional path's constraint check: for '[ReplOption] int count', a provider returning 'abc' and '42' offers only '42' (interactive).")]
	public async Task When_PendingOptionProviderValueViolatesType_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.WithCompletion("count", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["abc", "42"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --count ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("42", because: "42 converts to int");
		values.Should().NotContain("abc", because: "'abc' cannot bind to an int option, so it must not be offered");
	}

	[TestMethod]
	[Description("Shell parity: a pending option provider value violating the option type is filtered on the bridge too ('app run --count ' with int count offers only 42).")]
	public async Task When_PendingOptionProviderValueViolatesType_Then_ShellDoesNotOfferIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.WithCompletion(
				"count",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["abc", "42"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app run --count ").ConfigureAwait(false);

		candidates.Should().Contain("42");
		candidates.Should().NotContain("abc");
	}

	[TestMethod]
	[Description("A positional segment left unconstrained but bound to a narrower HANDLER type is validated against that type: 'run {count}' with handler (int count) and a provider returning 'abc'/'42' offers only '42', because 'abc' would fail int binding at execution even though the String segment constraint accepts it.")]
	public async Task When_PositionalValueViolatesHandlerType_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run {count}", static string (int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.WithCompletion("count", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["abc", "42"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("42");
		values.Should().NotContain("abc", because: "'abc' cannot bind to the int handler parameter");
	}

	[TestMethod]
	[Description("Under case-SENSITIVE option parsing, a pending enum option's provider value that differs only by case from a member ('prod' vs 'Prod') is dropped, because execution parses the enum with ignoreCase:false and would fail to bind it.")]
	public async Task When_PendingEnumOptionValueViolatesCase_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Options(static o => o.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive);
		sut.Map("run", static string ([ReplOption] ProbeMode mode) => mode.ToString())
			.WithCompletion("mode", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["prod", "Prod"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --mode ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("Prod", because: "the exact-case member binds");
		values.Should().NotContain("prod", because: "case-sensitive parsing would reject the lowercase spelling");
	}

	[TestMethod]
	[Description("A provider value that is an AMBIGUOUS command prefix is dropped: with 'pick {name}' and literal siblings 'pick status'/'pick staging', a provider value 'st' matches both literals, so execution stops at the ambiguous-prefix error before {name} is bound — it must not be offered.")]
	public async Task When_ProviderValueIsAmbiguousPrefix_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("pick status", static string () => "status").WithDescription("Pick status.");
		sut.Map("pick staging", static string () => "staging").WithDescription("Pick staging.");
		sut.Map("pick {name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["st", "alice"]))
			.WithDescription("Pick by name.");

		var result = await ResolveAutocompleteAsync(sut, "pick s").ConfigureAwait(false);

		var providerValues = result.Suggestions
			.Where(static s => s.Kind == ConsoleLineReader.AutocompleteSuggestionKind.Parameter)
			.Select(static s => s.Value)
			.ToArray();
		providerValues.Should().NotContain("st", because: "'st' is an ambiguous prefix; execution errors before binding {name}");
	}

	[TestMethod]
	[Description("Under case-SENSITIVE parsing, a POSITIONAL enum segment's provider value that differs only by case from a member ('prod' vs 'Prod') is dropped: execution binds the route value with the effective enum casing (HandlerArgumentBinder.ResolveEnumIgnoreCase), which for a pure segment follows the parsing default — ignoreCase:false here — so the lowercase spelling would fail to bind.")]
	public async Task When_PositionalEnumValueViolatesCase_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Options(static o => o.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive);
		sut.Map("run {mode}", static string (ProbeMode mode) => mode.ToString())
			.WithCompletion("mode", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["prod", "Prod"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("Prod", because: "the exact-case member binds to the enum positional");
		values.Should().NotContain("prod", because: "case-sensitive parsing rejects the lowercase spelling at execution");
	}

	[TestMethod]
	[Description("Shell parity: under case-sensitive parsing, a shell-scoped positional enum provider value differing only by case ('prod') is dropped on the bridge too, while the exact-case member ('Prod') is offered.")]
	public async Task When_PositionalEnumValueViolatesCase_Then_ShellDoesNotOfferIt()
	{
		var sut = CoreReplApp.Create();
		sut.Options(static o => o.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive);
		sut.Map("run {mode}", static string (ProbeMode mode) => mode.ToString())
			.WithCompletion(
				"mode",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["prod", "Prod"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app run ").ConfigureAwait(false);

		candidates.Should().Contain("Prod", because: "the exact-case member binds to the enum positional");
		candidates.Should().NotContain("prod", because: "case-sensitive parsing rejects the lowercase spelling at execution");
	}

	[TestMethod]
	[Description("A provider value whose token is REWRITTEN by unique-prefix expansion at the provider position is dropped even when the provider's own route stays the winner: with 'pick {name}' (provider returns 'sta') and an INCOMPLETE sibling 'pick status {id}', execution expands 'sta' to the literal 'status' before routing, then binds 'pick {name}' with 'status' — NOT the 'sta' the provider offered. Offering it would silently change the accepted value.")]
	public async Task When_ProviderValueRewrittenByPrefixExpansion_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("pick status {id}", static string (string id) => id).WithDescription("Pick status by id.");
		sut.Map("pick {name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["sta", "alice"]))
			.WithDescription("Pick by name.");

		var result = await ResolveAutocompleteAsync(sut, "pick s").ConfigureAwait(false);

		var providerValues = result.Suggestions
			.Where(static s => s.Kind == ConsoleLineReader.AutocompleteSuggestionKind.Parameter)
			.Select(static s => s.Value)
			.ToArray();
		providerValues.Should().NotContain("sta",
			because: "prefix expansion rewrites 'sta' to 'status', so accepting it binds a different value than offered");
		providerValues.Should().Contain("alice",
			because: "an un-rewritten value that binds to {name} is still offered");
	}

	[TestMethod]
	[Description("Shell parity: a provider value rewritten by unique-prefix expansion at the provider position is dropped on the bridge too — 'app pick s' with provider 'sta' and incomplete sibling 'pick status {id}' does not offer 'sta'.")]
	public async Task When_ProviderValueRewrittenByPrefixExpansion_Then_ShellDoesNotOfferIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("pick status {id}", static string (string id) => id).WithDescription("Pick status by id.");
		sut.Map("pick {name}", static string (string name) => name)
			.WithCompletion(
				"name",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["sta", "alice"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Pick by name.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app pick s").ConfigureAwait(false);

		candidates.Should().NotContain("sta",
			because: "prefix expansion rewrites 'sta' to 'status', so accepting it binds a different value than offered");
		candidates.Should().Contain("alice");
	}

	[TestMethod]
	[Description("Parity with the shell bridge: when an enum-typed pending option's provider FAULTS during an explicit Tab, the interactive path falls through to the static enum members instead of returning nothing — a transient provider failure must not hide the always-valid enum values ('run --mode ' still offers Debug/Prod).")]
	public async Task When_PendingEnumOptionProviderFaults_Then_EnumFallbackIsOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] ProbeMode mode) => mode.ToString())
			.WithCompletion("mode", static (_, _, _) => throw new InvalidOperationException("probe"))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --mode ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("Debug").And.Contain("Prod",
			because: "a faulting provider must fall through to the enum fallback, like the shell bridge");
	}

	[TestMethod]
	[Description("Shell parity guard for the same scenario: a faulting shell-scoped provider on an enum pending option still yields the enum members through the bridge (the deadline/fault wrapper returns no result, so the enum fallback runs).")]
	public async Task When_PendingEnumOptionProviderFaults_Then_ShellOffersEnumFallback()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] ProbeMode mode) => mode.ToString())
			.WithCompletion(
				"mode",
				static (_, _, _) => throw new InvalidOperationException("probe"),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app run --mode ").ConfigureAwait(false);

		candidates.Should().Contain("Debug").And.Contain("Prod",
			because: "a faulting provider must not suppress the enum fallback on the bridge");
	}

	[TestMethod]
	[Description("A dynamic route segment bound to a COLLECTION handler parameter never binds a single value at execution: HandlerArgumentBinder takes route values via ConvertSingle(routeValue, IReadOnlyList<int>), which throws for a collection target, so no single candidate can bind. The provider path must suppress such candidates rather than validate them against the unwrapped element type.")]
	public async Task When_PositionalProviderTargetsCollectionParam_Then_ValueIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run {ids}", static string (IReadOnlyList<int> ids) => string.Join(',', ids))
			.WithCompletion("ids", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["42"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run ").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("42",
			because: "a single route value cannot bind to a collection parameter, so the candidate can never execute");
	}

	[TestMethod]
	[Description("Shell parity: a collection-typed route segment suppresses provider candidates on the bridge too, since the shared handler-type check binds route values with ConvertSingle against the whole (collection) type.")]
	public async Task When_PositionalProviderTargetsCollectionParam_Then_ShellDoesNotOfferIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run {ids}", static string (IReadOnlyList<int> ids) => string.Join(',', ids))
			.WithCompletion(
				"ids",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["42"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app run ").ConfigureAwait(false);

		candidates.Should().NotContain("42",
			because: "a single route value cannot bind to a collection parameter at execution");
	}

	[TestMethod]
	[Description("A first-token provider value that collides with an ambient command ('help', a custom ambient) is not offered: CommittedInputResolver dispatches ambients BEFORE routing, so accepting it runs the ambient handler instead of binding to the route's first dynamic segment. Non-ambient values still bind and are offered.")]
	public async Task When_ProviderValueShadowedByAmbient_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Options(static o => o.AmbientCommands.MapAmbient("teleport", static () => { }, "Teleport."));
		sut.Map("{name}", static string (string name) => name)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["help", "teleport", "zebra"]))
			.WithDescription("Name.");

		var result = await ResolveAutocompleteAsync(sut, "z").ConfigureAwait(false);

		var providerValues = result.Suggestions
			.Where(static s => s.Kind == ConsoleLineReader.AutocompleteSuggestionKind.Parameter)
			.Select(static s => s.Value)
			.ToArray();
		providerValues.Should().NotContain("help", because: "'help' is an ambient command dispatched before routing");
		providerValues.Should().NotContain("teleport", because: "a custom ambient is dispatched before routing");
		providerValues.Should().Contain("zebra", because: "a non-ambient value still binds to the first dynamic segment");
	}

	[TestMethod]
	[Description("An incomplete provider route's value that is shadowed by an EXACT context is not offered: with 'pick {name} {id}' and a context 'pick status', the value 'status' would navigate/render the context (dispatch checks exact contexts when no terminal route matches) instead of binding {name}. A value with no colliding context still binds.")]
	public async Task When_ProviderValueShadowedByExactContext_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Context("pick status", static scope => scope.Map("info", static string () => "ok").WithDescription("Info."));
		sut.Map("pick {name} {id}", static string (string name, string id) => name + id)
			.WithCompletion("name", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["status", "alice"]))
			.WithDescription("Pick by name and id.");

		var result = await ResolveAutocompleteAsync(sut, "pick s").ConfigureAwait(false);

		var providerValues = result.Suggestions
			.Where(static s => s.Kind == ConsoleLineReader.AutocompleteSuggestionKind.Parameter)
			.Select(static s => s.Value)
			.ToArray();
		providerValues.Should().NotContain("status", because: "'pick status' navigates the exact context, so the value never binds to {name}");
		providerValues.Should().Contain("alice", because: "'alice' has no colliding context and binds to {name}");
	}

	[TestMethod]
	[Description("On the shell bridge, a pending option's provider value starting with '@' is dropped when response files are enabled (the default for non-interactive execution): InvocationOptionParser expands an '@file' token before binding, so the completed command would read a response file instead of using the literal value.")]
	public async Task When_ShellPendingOptionValueLooksLikeResponseFile_Then_ItIsDropped()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? file) => file ?? "none")
			.WithCompletion(
				"file",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["@prod", "normal"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app run --file ").ConfigureAwait(false);

		candidates.Should().Contain("normal");
		candidates.Should().NotContain(static c => c.Contains("prod", StringComparison.Ordinal),
			because: "an '@'-prefixed option value would be expanded as a response file by the parser before binding");
	}

	[TestMethod]
	[Description("Interactive parity is INVERTED here by design: the interactive session does not expand response files (AllowResponseFiles is off for interactive execution), so a pending option provider value '@prod' is a literal value and IS offered.")]
	public async Task When_InteractivePendingOptionValueLooksLikeResponseFile_Then_ItIsOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? file) => file ?? "none")
			.WithCompletion("file", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["@prod", "normal"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --file ").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("@prod",
			because: "interactive execution does not expand response files, so '@prod' binds as a literal value");
	}

	[TestMethod]
	[Description("A dynamic segment sharing its name with a handler parameter that binds BEFORE route values ([FromServices]) must not have provider candidates validated against that unrelated type: 'use {id}' with ([FromServices] IRepo id) ignores the route value for the service parameter, so a normal string id is offered, not dropped for failing to convert to IRepo.")]
	public async Task When_SegmentNameMatchesServiceBoundParam_Then_ValueIsStillOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("use {id}", static string ([FromServices] IRepo id) => id.Marker)
			.WithCompletion("id", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["client-1"]))
			.WithDescription("Use.");

		var result = await ResolveAutocompleteAsync(sut, "use ").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("client-1",
			because: "execution binds the [FromServices] parameter from DI and ignores the route value, so any string id is valid");
	}

	[TestMethod]
	[Description("Shell parity: a service-bound segment parameter still offers normal provider values through the bridge (the shared handler-type check skips explicitly-bound parameters).")]
	public async Task When_SegmentNameMatchesServiceBoundParam_Then_ShellStillOffersIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("use {id}", static string ([FromServices] IRepo id) => id.Marker)
			.WithCompletion(
				"id",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["client-1"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Use.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app use ").ConfigureAwait(false);

		candidates.Should().Contain("client-1",
			because: "the route value is ignored for the [FromServices] parameter, so the candidate binds");
	}

	[TestMethod]
	[Description("Shell parity for the ambient shadow, scoped to the CLI-preempted set: a shell-scoped root provider value equal to a CLI ambient ('complete' any-count, 'exit'/'..' at a single token) is dropped, because a non-interactive run dispatches those first tokens before routing. Tokens the CLI does NOT preempt (e.g. a plain value) still bind and complete.")]
	public async Task When_ShellProviderValueShadowedByCliAmbient_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("{name}", static string (string name) => name)
			.WithCompletion(
				"name",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["complete", "exit", "..", "zebra"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Name.");
		var shellEngine = new ShellCompletionEngine(sut);

		var candidates = await ResolveShellCandidatesAsync(shellEngine, "app z").ConfigureAwait(false);

		candidates.Should().NotContain("complete", because: "'complete' is dispatched as the CLI completion ambient before routing");
		candidates.Should().NotContain("exit", because: "'exit' is a CLI ambient at a single first token");
		candidates.Should().NotContain("..", because: "'..' is a CLI ambient at a single first token");
		candidates.Should().Contain("zebra", because: "a non-ambient value still binds to the first dynamic segment");
	}

	[TestMethod]
	[Description("A dynamic segment sharing its name with a [ReplOptionsGroup] handler parameter (bound before route values) must not have provider candidates validated against the group type: 'use {opts}' with (UseOptionsGroup opts) ignores the route value, so a normal string is offered.")]
	public async Task When_SegmentNameMatchesOptionsGroupParam_Then_ValueIsStillOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("use {opts}", static string (UseOptionsGroup opts) => opts.Marker ?? "none")
			.WithCompletion("opts", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["client-1"]))
			.WithDescription("Use.");

		var result = await ResolveAutocompleteAsync(sut, "use ").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("client-1",
			because: "an options-group parameter is bound before route values, so the route value is ignored and any string is valid");
	}

	[TestMethod]
	[Description("A dynamic segment sharing its name with a typed global-options handler parameter (UseGlobalOptions<T>, bound before route values) must not have provider candidates validated against T: 'use {tenant}' with (TenantGlobals tenant) injects the typed options and ignores the route value, so a normal string is offered.")]
	public async Task When_SegmentNameMatchesTypedGlobalOptionsParam_Then_ValueIsStillOffered()
	{
		var sut = CoreReplApp.Create();
		sut.RegisterGlobalOptionsType(typeof(TenantGlobals));
		sut.Map("use {tenant}", static string (TenantGlobals tenant) => tenant.Name ?? "none")
			.WithCompletion("tenant", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["acme"]))
			.WithDescription("Use.");

		var result = await ResolveAutocompleteAsync(sut, "use ").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("acme",
			because: "a typed global-options parameter is injected before route values, so the route value is ignored");
	}

	private interface IRepo
	{
		string Marker { get; }
	}

	[ReplOptionsGroup]
	private sealed class UseOptionsGroup
	{
		public string? Marker { get; init; }
	}

	private sealed class TenantGlobals
	{
		public string? Name { get; init; }
	}

	private enum ProbeMode
	{
		Debug,
		Prod,
	}

	private static readonly string[] s_dashTargets = ["-prod", "-staging"];
	private static readonly string[] s_intIds = ["42", "77"];
	private static readonly string[] s_names = ["alice", "bob"];

	private static async Task<string[]> ResolveShellCandidatesAsync(
		ShellCompletionEngine engine,
		string line,
		ShellKind shell = ShellKind.Bash) =>
		await engine.ResolveShellCompletionCandidatesAsync(
				line,
				line.Length,
				shell,
				EmptyServiceProvider.Instance,
				CancellationToken.None)
			.ConfigureAwait(false);

	[TestMethod]
	[Description("A live-hint refresh (MenuRequested: false, issued after every keystroke) must NOT await value providers: a slow provider would otherwise freeze typing once per edit. Providers only run for an explicit completion request (Tab/menu).")]
	public async Task When_LiveHintRefreshesWhileTypingValue_Then_ProviderIsNotInvoked()
	{
		var invocationCount = 0;
		var sut = CoreReplApp.Create();
		sut.Map("contact inspect {clientId}", static string (string clientId) => clientId)
			.WithCompletion("clientId", (_, _, _) =>
			{
				invocationCount++;
				return ValueTask.FromResult<IReadOnlyList<string>>(["zo-ga"]);
			})
			.WithDescription("Inspect a contact.");

		await ResolveAutocompleteAsync(sut, "contact inspect ab", menuRequested: false).ConfigureAwait(false);

		invocationCount.Should().Be(0, because: "live-hint refreshes happen per keystroke and must stay provider-free");
	}

	[TestMethod]
	[Description("The pending option value path obeys the same rule: a live-hint refresh (MenuRequested: false) after 'run --channel ' must not await the option's provider — only an explicit Tab/menu request may.")]
	public async Task When_LiveHintRefreshesOnPendingOptionValue_Then_ProviderIsNotInvoked()
	{
		var invocationCount = 0;
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none")
			.WithCompletion("channel", (_, _, _) =>
			{
				invocationCount++;
				return ValueTask.FromResult<IReadOnlyList<string>>(["alpha"]);
			})
			.WithDescription("Run.");

		await ResolveAutocompleteAsync(sut, "run --channel ", menuRequested: false).ConfigureAwait(false);

		invocationCount.Should().Be(0, because: "live-hint refreshes happen per keystroke and must stay provider-free");
	}

	private static async Task<ConsoleLineReader.AutocompleteResult> ResolveAutocompleteAsync(
		CoreReplApp app,
		string input,
		IReadOnlyList<string>? scopeTokens = null,
		bool menuRequested = true)
	{
		var result = await app.Autocomplete.ResolveAutocompleteAsync(
			new ConsoleLineReader.AutocompleteRequest(input, input.Length, menuRequested, ExplicitCompletion: menuRequested),
			scopeTokens ?? [],
			EmptyServiceProvider.Instance,
			CancellationToken.None)
			.ConfigureAwait(false);
		result.Should().NotBeNull();
		return result!.Value;
	}

	private sealed class EmptyServiceProvider : IServiceProvider
	{
		public static readonly EmptyServiceProvider Instance = new();
		public object? GetService(Type serviceType) => null;
	}
}
