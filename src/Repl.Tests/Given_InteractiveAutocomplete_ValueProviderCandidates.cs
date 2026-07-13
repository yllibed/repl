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

	private static async Task<string[]> ResolveShellCandidatesAsync(ShellCompletionEngine engine, string line) =>
		await engine.ResolveShellCompletionCandidatesAsync(
				line,
				line.Length,
				EmptyServiceProvider.Instance,
				CancellationToken.None)
			.ConfigureAwait(false);

	private static async Task<ConsoleLineReader.AutocompleteResult> ResolveAutocompleteAsync(
		CoreReplApp app,
		string input,
		IReadOnlyList<string>? scopeTokens = null)
	{
		var result = await app.Autocomplete.ResolveAutocompleteAsync(
			new ConsoleLineReader.AutocompleteRequest(input, input.Length, MenuRequested: true),
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
