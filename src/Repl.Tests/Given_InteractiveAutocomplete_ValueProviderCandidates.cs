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
