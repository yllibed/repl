using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Repl.ShellCompletion;

namespace Repl.Tests;

[TestClass]
public sealed class Given_AdvancedInput
{
	[TestMethod]
	[Description("Legacy syntax must reach adaptation before parsing, without restarting the chain after a rewrite.")]
	public async Task When_LegacyArgumentsAreRewritten_Then_AdaptersRunOnceBeforeParsing()
	{
		var app = CoreReplApp.Create();
		var calls = new List<string>();
		app.UseArgumentPreprocessor((input, _) =>
		{
			calls.Add("legacy");
			input.OriginalArguments.Should().Equal("/find", "hello world");
			return ValueTask.FromResult(ArgumentPreprocessingResult.Replace(["query", input.Arguments[1]]));
		});
		app.UseArgumentPreprocessor((input, _) =>
		{
			calls.Add("normalized");
			input.Arguments.Should().Equal("query", "hello world");
			return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
		});
		app.Map("query {text}", (string text) => { calls.Add(text); });

		var code = await app.RunAsync(["/find", "hello world"]);

		code.Should().Be(0);
		calls.Should().Equal("legacy", "normalized", "hello world");
	}

	[TestMethod]
	[Description("A handled decision is terminal even when no matching Repl route exists.")]
	public async Task When_PreprocessorHandlesInput_Then_NoRouteOrLaterAdapterIsRequired()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Handled(Results.Exit(17))));
		app.UseArgumentPreprocessor((_, _) => throw new InvalidOperationException("must not run"));

		(await app.RunAsync(["/external"])).Should().Be(17);
	}

	[TestMethod]
	[Description("Analysis must reject invalid input without constructing execution-only handler dependencies.")]
	public async Task When_TypedValidationRejects_Then_HandlerDependenciesAreNotConstructed()
	{
		var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
		var created = false;
		Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddTransient<HandlerDependency>(
			services, _ => { created = true; return new HandlerDependency(); });
		var app = ReplApp.Create(collection =>
		{
			foreach (var descriptor in services) { collection.Add(descriptor); }
		});
		app.UseInputValidator((ParsedInputContext input) =>
		{
			input.TryGetValue<int>("limit", out var limit).Should().BeTrue();
			return limit > 10 ? InputValidationResult.Invalid("Limit exceeds ten.") : InputValidationResult.Valid();
		});
		app.Map("query", (int limit, [FromServices] HandlerDependency dependency) => limit);

		(await app.RunAsync(["query", "--limit", "12"])).Should().Be(2);
		created.Should().BeFalse();
	}

	private sealed class HandlerDependency;

	[TestMethod]
	[Description("Callback defects use the application-failure outcome rather than blaming user input.")]
	public async Task When_AdapterThrows_Then_ItIsAnApplicationFailure()
	{
		var app = CoreReplApp.Create();
		ReplExecutionOutcomeKind? kind = null;
		app.Options(options => options.ExitCodes.Resolver = outcome => { kind = outcome.Kind; return outcome.ExitCode; });
		app.UseArgumentPreprocessor((_, _) => throw new InvalidOperationException("adapter failed"));
		(await app.RunAsync(["old"])).Should().Be(1);
		kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
	}

	[TestMethod]
	[Description("Caller cancellation must prevent callback side effects before the chain starts.")]
	public async Task When_InputIsAlreadyCancelled_Then_AdaptersDoNotRun()
	{
		var app = CoreReplApp.Create();
		app.Options(options => options.ExitCodes.Cancelled = 130);
		app.UseArgumentPreprocessor((_, _) => throw new InvalidOperationException("must not run"));
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();
		(await app.RunAsync([], cancellation.Token)).Should().Be(130);
	}

	[TestMethod]
	[Description("Callbacks need the help purpose to guard execution-only work on help requests.")]
	public async Task When_HelpIsRequested_Then_AdaptersAndValidatorsSeeItsPurpose()
	{
		var app = CoreReplApp.Create();
		var purposes = new List<InputPurpose>();
		app.UseArgumentPreprocessor((input, _) =>
		{
			purposes.Add(input.Purpose);
			return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
		});
		app.UseInputValidator((ParsedInputContext input) =>
		{
			purposes.Add(input.Input.Purpose);
			return InputValidationResult.Valid();
		});
		app.Map("query", int () => throw new InvalidOperationException("must not run"));
		(await app.RunAsync(["query", "--help"])).Should().Be(0);
		purposes.Should().Equal(InputPurpose.Help, InputPurpose.Help);
	}

	[TestMethod]
	[Description("Validator injection must use the invocation scope that the eventual handler receives.")]
	public async Task When_ValidatorNeedsAScopedService_Then_TheHandlerUsesTheSameScope()
	{
		var app = ReplApp.Create(services => services.AddScoped<HandlerDependency>());
		HandlerDependency? validated = null;
		app.UseInputValidator((ParsedInputContext _, HandlerDependency dependency, CancellationToken ct) =>
		{
			ct.IsCancellationRequested.Should().BeFalse();
			validated = dependency;
			return ValueTask.FromResult(InputValidationResult.Valid());
		});
		app.Map("query", ([FromServices] HandlerDependency dependency) => dependency.Should().BeSameAs(validated));
		(await app.RunAsync(["query"])).Should().Be(0);
		validated.Should().NotBeNull();
	}

	[TestMethod]
	[Description("A valid enum prefix needs more typing rather than an invalid-input diagnostic.")]
	public async Task When_AnEnumPrefixCanStillBecomeValid_Then_PreviewIsIncomplete()
	{
		var app = CoreReplApp.Create();
		app.Map("query", (ProbeMode mode) => mode);
		var incomplete = await AnalyzeAsync(app, "query --mode D");
		var invalid = await AnalyzeAsync(app, "query --mode Z");
		var valid = await AnalyzeAsync(app, "query --mode Debug");
		incomplete.Validation.State.Should().Be(InputValidationState.Incomplete);
		invalid.Validation.State.Should().Be(InputValidationState.Invalid);
		valid.Validation.State.Should().Be(InputValidationState.Valid);
	}

	[TestMethod]
	[Description("A missing typed value must not be confused with a failed conversion or a default numeric value.")]
	public async Task When_ARequiredValueIsMissing_Then_TheSnapshotDistinguishesItFromInvalidInput()
	{
		var app = CoreReplApp.Create();
		app.Map("query", (int limit) => limit);
		var missing = await AnalyzeAsync(app, "query");
		var invalid = await AnalyzeAsync(app, "query --limit invalid");
		missing.Values["limit"].State.Should().Be(InputValueState.Missing);
		missing.Validation.State.Should().Be(InputValidationState.Incomplete);
		invalid.Values["limit"].State.Should().Be(InputValueState.Invalid);
		invalid.Validation.State.Should().Be(InputValidationState.Invalid);
	}

	[TestMethod]
	[Description("Incomplete application checks must stop submitted execution as well as inform live analysis.")]
	public async Task When_TypedValidationReturnsIncomplete_Then_SubmissionIsRefused()
	{
		var app = CoreReplApp.Create();
		app.UseInputValidator((ParsedInputContext _) => InputValidationResult.Incomplete("A check is still required."));
		app.Map("query", int () => throw new InvalidOperationException("must not run"));
		(await app.RunAsync(["query"])).Should().Be(2);
	}

	[TestMethod]
	[Description("Ordered validators must receive the accumulated diagnostic context.")]
	public async Task When_ValidatorsRunInOrder_Then_LaterValidatorsSeeEarlierDiagnostics()
	{
		var app = CoreReplApp.Create();
		app.Map("query", () => "ok");
		app.UseInputValidator((ParsedInputContext _) => InputValidationResult.Invalid("first check"));
		app.UseInputValidator((ParsedInputContext input) =>
		{
			input.Validation.Diagnostics.Select(item => item.Message).Should().Contain("first check");
			return InputValidationResult.Valid();
		});
		(await app.RunAsync(["query"])).Should().Be(2);
	}

	[TestMethod]
	[Description("Canonical text must preserve argument boundaries and contents through tokenization.")]
	public void When_ArgumentsAreFormatted_Then_EmptyValuesAndQuotesRoundTrip()
	{
		string[] values = ["query", "", "hello world", "a'b\"c", "C:\\some path\\", "--not-an-option", "é😀"];
		var formatted = InputTokenizer.Format(values);
		InteractiveSession.TokenizeInteractiveInput(formatted).Should().Equal(values);
	}

	[TestMethod]
	[Description("Converting legacy text on Tab must preserve a quoted multiword argument.")]
	public async Task When_TabNormalizesLegacyInput_Then_ItPreservesAllArguments()
	{
		var app = CreateLegacyApp();
		var assist = await app.Autocomplete.ResolveAutocompleteAsync(
			new("/find \"hello world\"", 19, MenuRequested: false, ExplicitCompletion: true), [], app.CurrentServiceProvider, CancellationToken.None);
		assist.Should().NotBeNull();
		InputTokenizer.Tokenize(assist!.Value.NormalizedInput!).Select(token => token.Value).Should().Equal("query", "hello world");
	}

	[TestMethod]
	[Description("Applications can retain their legacy editor syntax by opting out of normalization.")]
	public async Task When_TabNormalizationIsDisabled_Then_LegacyTextIsRetained()
	{
		var app = CreateLegacyApp();
		app.Options(options => options.Interactive.Autocomplete.NormalizeInputOnTab = false);
		var assist = await app.Autocomplete.ResolveAutocompleteAsync(
			new("/find hello", 11, MenuRequested: false, ExplicitCompletion: true), [], app.CurrentServiceProvider, CancellationToken.None);
		assist!.Value.NormalizedInput.Should().BeNull();
	}

	[TestMethod]
	[Description("Mapped diagnostics must highlight the original value rather than an unrelated canonical token.")]
	public async Task When_AdapterMapsARewrittenValue_Then_DiagnosticUsesTheOriginalTextRange()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(
			ArgumentPreprocessingResult.Replace(["query", "--limit", "wrong"], [null, null, new(3, 5)])));
		app.Map("query", (int limit) => limit);
		var analysis = await AnalyzeAsync(app, "/n:wrong");
		analysis.Validation.Diagnostics.Should().Contain(item => item.Range == new InputSourceRange(3, 5));
	}

	[TestMethod]
	[Description("Typing must never render or execute a terminal result returned by an adapter.")]
	public async Task When_PreviewHasAnExitResult_Then_ItDoesNotRenderItsPayload()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Handled(Results.Exit(9, "must not print"))));
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		var assist = await app.Autocomplete.ResolveAutocompleteAsync(new("old", 3, MenuRequested: false),
			[], app.CurrentServiceProvider, CancellationToken.None);
		assist.Should().NotBeNull();
		writer.ToString().Should().BeEmpty();
	}

	[TestMethod]
	[Description("Preview globals are temporary and must not leak into later committed invocations.")]
	public async Task When_PreviewChangesGlobals_Then_ItDoesNotChangeTheCommittedBaseline()
	{
		var app = CoreReplApp.Create();
		app.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		app.Map("query", () => "ok");
		await app.RunAsync(["query", "--tenant", "baseline"]);
		var analysis = await AnalyzeAsync(app, "query --tenant preview");
		analysis.GlobalValues["tenant"].Value.Should().Be("preview");
		app.GlobalOptionsAccessor.GetValue<string>("tenant").Should().Be("baseline");
	}

	[TestMethod]
	[Description("Concurrent analyses must isolate globals observed through injected services.")]
	public async Task When_PreviewServicesReadGlobalsConcurrently_Then_EachValidatorSeesItsOwnValues()
	{
		var app = ReplApp.Create();
		app.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		app.Map("query", () => "ok");
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var count = 0;
		app.UseInputValidator(async (ParsedInputContext input, IGlobalOptionsAccessor globals) =>
		{
			if (Interlocked.Increment(ref count) == 2) { ready.TrySetResult(); }
			#pragma warning disable VSTHRD003 // The test controls both concurrent validator invocations.
			await release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			globals.GetValue<string>("tenant").Should().Be((string)input.GlobalValues["tenant"].Value!);
			return InputValidationResult.Valid();
		});
		var first = AnalyzeAsync(app.Core, "query --tenant first", app.Services);
		var second = AnalyzeAsync(app.Core, "query --tenant second", app.Services);
		await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
		release.SetResult();
		var results = await Task.WhenAll(first, second);
		results.Should().OnlyContain(result => result.Validation.State == InputValidationState.Valid);
	}

	[TestMethod]
	[Description("Handling a single invocation must not close the surrounding interactive session.")]
	public async Task When_AHandledInteractiveCommandCompletes_Then_TheSessionAcceptsTheNextCommand()
	{
		var app = CoreReplApp.Create();
		var ran = false;
		app.UseArgumentPreprocessor((input, _) => ValueTask.FromResult(
			input.Arguments.Count != 0 && string.Equals(input.Arguments[0], "/external", StringComparison.Ordinal)
				? ArgumentPreprocessingResult.Handled("external done") : ArgumentPreprocessingResult.Continue()));
		app.Map("next", () => { ran = true; });
		using var writer = new StringWriter();
		using var reader = new StringReader("/external\nnext\nexit\n");
		using var io = ReplSessionIO.SetSession(writer, reader);
		(await app.RunAsync([])).Should().Be(0);
		ran.Should().BeTrue();
		writer.ToString().Should().Contain("external done");
	}

	[TestMethod]
	[Description("An adapter rewrites editable arguments while navigation scope remains separate.")]
	public async Task When_InteractiveInputIsRewrittenInsideAScope_Then_TheScopeIsNotDuplicated()
	{
		var app = CoreReplApp.Create();
		var ran = false;
		app.UseArgumentPreprocessor((input, _) => ValueTask.FromResult(
			input.Arguments.Count != 0 && string.Equals(input.Arguments[0], "/next", StringComparison.Ordinal)
				? ArgumentPreprocessingResult.Replace(["next"]) : ArgumentPreprocessingResult.Continue()));
		app.Context("tools", scope => scope.Map("next", () => { ran = true; }));
		using var writer = new StringWriter();
		using var reader = new StringReader("tools\n/next\nexit\n");
		using var io = ReplSessionIO.SetSession(writer, reader);
		(await app.RunAsync([])).Should().Be(0);
		ran.Should().BeTrue();
	}

	[TestMethod]
	[Description("Shell callbacks must analyze user input in completion mode rather than the protocol envelope.")]
	public async Task When_ShellCompletionUsesLegacySyntax_Then_AdaptersSeeTheTransportedLine()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((input, _) =>
		{
			input.Mode.Should().Be(InputProcessingMode.ShellCompletion);
			input.Arguments[0].Should().Be("/find");
			return ValueTask.FromResult(ArgumentPreprocessingResult.Replace(["query", .. input.Arguments.Skip(1)]));
		});
		app.Map("query", (ProbeMode mode) => mode);
		var line = "app /find --mode D";
		var candidates = await new ShellCompletionEngine(app).ResolveShellCompletionCandidatesAsync(
			line, line.Length, ShellKind.Bash, app.CurrentServiceProvider, CancellationToken.None);
		candidates.Should().Contain("Debug");
	}

	private static CoreReplApp CreateLegacyApp()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((input, _) => ValueTask.FromResult(
			input.Arguments.Count != 0 && string.Equals(input.Arguments[0], "/find", StringComparison.Ordinal)
				? ArgumentPreprocessingResult.Replace(["query", .. input.Arguments.Skip(1)])
				: ArgumentPreprocessingResult.Continue()));
		app.Map("query {text}", (string text) => text);
		return app;
	}

	private static async Task<ParsedInputContext> AnalyzeAsync(CoreReplApp app, string text, IServiceProvider? services = null)
	{
		var input = await app.PreprocessInputAsync(InputTokenizer.Tokenize(text).Select(token => token.Value).ToArray(),
			[], InputProcessingMode.Preview, text, text.Length, CancellationToken.None).ConfigureAwait(false);
		return await app.AnalyzeInputAsync(input, services ?? app.CurrentServiceProvider, CancellationToken.None).ConfigureAwait(false);
	}

	private enum ProbeMode { Debug, Release }

	[TestMethod]
	[Description("Live analysis and execution must share named-versus-positional conflict rules.")]
	public async Task When_NamedAndPositionalValuesConflict_Then_AnalysisAndExecutionBothReject()
	{
		var app = CoreReplApp.Create();
		app.Map("query", (int limit) => limit);
		var analysis = await AnalyzeAsync(app, "query --limit 1 2");
		analysis.Validation.State.Should().Be(InputValidationState.Invalid);
		analysis.Values["limit"].State.Should().Be(InputValueState.Invalid);
		(await app.RunAsync(["query", "--limit", "1", "2"])).Should().Be(2);
	}

	[TestMethod]
	[Description("Explicit collection minimum arity must remain consistent between analysis and execution.")]
	public async Task When_AnExplicitCollectionMinimumIsNotMet_Then_AnalysisIsIncompleteAndSubmissionFails()
	{
		var app = CoreReplApp.Create();
		app.Map("query", ([Repl.Parameters.ReplOption(Arity = Repl.Parameters.ReplArity.OneOrMore)] int[] items) => items.Length);
		var analysis = await AnalyzeAsync(app, "query");
		analysis.Validation.State.Should().Be(InputValidationState.Incomplete);
		(await app.RunAsync(["query"])).Should().Be(2);
	}

	[TestMethod]
	[Description("Positional collection selection must enforce the same upper arity bound as binding.")]
	public async Task When_PositionalCollectionExceedsAnExplicitMaximum_Then_AnalysisRejectsIt()
	{
		var app = CoreReplApp.Create();
		app.Map("query", ([Repl.Parameters.ReplOption(Arity = Repl.Parameters.ReplArity.ZeroOrOne)] int[] items) => items.Length);
		var analysis = await AnalyzeAsync(app, "query 1 2");
		analysis.Validation.State.Should().Be(InputValidationState.Invalid);
		(await app.RunAsync(["query", "1", "2"])).Should().Be(2);
	}

	[TestMethod]
	[Description("Built-in diagnostics on a partial option must not suppress useful completion candidates.")]
	public async Task When_PartialOptionIsCompleted_Then_ShellAndEditorStillOfferIt()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Continue()));
		app.Map("query", (ProbeMode mode) => mode);
		var analysis = await AnalyzeAsync(app, "query --mo");
		analysis.Validation.State.Should().Be(InputValidationState.Incomplete);
		var line = "app query --mo";
		var shell = await new ShellCompletionEngine(app).ResolveShellCompletionCandidatesAsync(
			line, line.Length, ShellKind.Bash, app.CurrentServiceProvider, CancellationToken.None);
		shell.Should().Contain("--mode");
		var editor = await app.Autocomplete.ResolveAutocompleteAsync(new("query --mo", 10, MenuRequested: true, ExplicitCompletion: true),
			[], app.CurrentServiceProvider, CancellationToken.None);
		editor!.Value.Suggestions.Select(item => item.Value).Should().Contain("--mode");
	}

	[TestMethod]
	[Description("Help must honor incomplete application checks even though built-in missing values do not block help.")]
	public async Task When_CustomValidationNeedsMoreInputForHelp_Then_ItsDecisionIsHonored()
	{
		var app = CoreReplApp.Create();
		app.Map("query", (int limit) => limit);
		app.UseInputValidator((ParsedInputContext _) => InputValidationResult.Incomplete("Application requires more information."));
		(await app.RunAsync(["query", "--help"])).Should().Be(2);
	}

	[TestMethod]
	[Description("Help must remain available when users are asking how to supply missing command values.")]
	public async Task When_OnlyBuiltinChecksAreIncomplete_Then_HelpStillWorks()
	{
		var app = CoreReplApp.Create();
		app.Map("query", (int limit) => limit);
		app.UseInputValidator((ParsedInputContext _) => InputValidationResult.Valid());
		(await app.RunAsync(["query", "--help"])).Should().Be(0);
	}

	[TestMethod]
	[Description("Direct adapter results must honor the normal requested output format.")]
	public async Task When_HandledInputSelectsJson_Then_ItUsesNormalOutputFormatting()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Handled(new { Value = 7 })));
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		(await app.RunAsync(["/external", "--json"])).Should().Be(0);
		using var document = System.Text.Json.JsonDocument.Parse(writer.ToString());
		document.RootElement.GetProperty("value").GetInt32().Should().Be(7);
	}

	[TestMethod]
	[Description("Tuple rendering must retain an explicit exit outcome supplied by an adapter.")]
	public async Task When_HandledInputReturnsAnExitTuple_Then_TheExplicitCodeIsPreserved()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Handled(("done", Results.Exit(19)))));
		(await app.RunAsync(["/external"])).Should().Be(19);
	}
}
