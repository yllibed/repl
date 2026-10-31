using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Repl.ShellCompletion;

namespace Repl.Tests;

[TestClass]
public sealed class Given_AdvancedInput
{
	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Synchronous and asynchronous adapters share ordered rewrites and receive the caller's cancellation token on Core and Defaults.")]
	public async Task When_SyncAndAsyncAdaptersAreMixed_Then_RewritesReachTheHandler(bool defaults)
	{
		var app = ReplApp.Create();
		using var cancellation = new CancellationTokenSource();
		var calls = new List<string>();
		ArgumentPreprocessingResult Rewrite(ArgumentPreprocessingContext input, CancellationToken ct)
		{
			ct.Should().Be(cancellation.Token);
			input.Arguments.Should().Equal("/old");
			calls.Add("sync");
			return ArgumentPreprocessingResult.Replace(["query", "hello world"]);
		}
		async ValueTask<ArgumentPreprocessingResult> ObserveAsync(ArgumentPreprocessingContext input, CancellationToken ct)
		{
			await Task.Yield();
			ct.Should().Be(cancellation.Token);
			input.Arguments.Should().Equal("query", "hello world");
			calls.Add("async");
			return ArgumentPreprocessingResult.Continue();
		}
		if (defaults) { app.UseArgumentPreprocessor(Rewrite).UseArgumentPreprocessor(ObserveAsync); }
		else { app.Core.UseArgumentPreprocessor(Rewrite).UseArgumentPreprocessor(ObserveAsync); }
		app.Map("query {text}", (string text) => { calls.Add(text); });

		var code = defaults
			? await app.RunAsync(["/old"], cancellation.Token)
			: await app.Core.RunAsync(["/old"], cancellation.Token);

		code.Should().Be(0);
		calls.Should().Equal("sync", "async", "hello world");
	}

	[TestMethod]
	[Description("Adding synchronous overloads keeps untyped null calls unambiguous and rejects null synchronous callbacks at registration.")]
	public void When_PreprocessorIsNull_Then_RegistrationRejectsBothSignatures()
	{
		var app = ReplApp.Create();
		var builder = ReplStartup.CreateBuilder();
		Func<ArgumentPreprocessingContext, CancellationToken, ArgumentPreprocessingResult> callback = null!;
		Assert.Throws<ArgumentNullException>(() => app.Core.UseArgumentPreprocessor(null!));
		Assert.Throws<ArgumentNullException>(() => app.UseArgumentPreprocessor(null!));
		Assert.Throws<ArgumentNullException>(() => builder.UseArgumentPreprocessor(null!));
		Assert.Throws<ArgumentNullException>(() => app.Core.UseArgumentPreprocessor(callback));
		Assert.Throws<ArgumentNullException>(() => app.UseArgumentPreprocessor(callback));
		Assert.Throws<ArgumentNullException>(() => builder.UseArgumentPreprocessor(callback));
	}

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
	[Description("Verifies an input callback that throws for an invocation asking for JSON — an argument preprocessor, or a validator of the resolved command — reports its failure as JSON, so a program reading the output can still parse it.")]
	[DataRow(false, DisplayName = "Argument preprocessor")]
	[DataRow(true, DisplayName = "Input validator")]
	public async Task When_AnInputCallbackThrowsForJsonOutput_Then_TheFailureIsReportedAsJson(bool validator)
	{
		var app = CoreReplApp.Create();
		ReplExecutionOutcomeKind? kind = null;
		app.Options(options => options.ExitCodes.Resolver = outcome => { kind = outcome.Kind; return outcome.ExitCode; });
		app.Map("old", () => "ok");
		if (validator)
		{
			app.UseInputValidator((Func<ParsedInputContext, InputValidationResult>)(_ => throw new InvalidOperationException("validator failed")));
		}
		else
		{
			app.UseArgumentPreprocessor((_, _) => throw new InvalidOperationException("adapter failed"));
		}

		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);

		(await app.RunAsync(["old", "--json"])).Should().Be(1);
		kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
		using var document = System.Text.Json.JsonDocument.Parse(writer.ToString());
		document.RootElement.GetRawText().Should().Contain("input_callback_error");
	}

	[TestMethod]
	[Description("Verifies the format of an input callback's failure report follows the input as far as it was processed: an earlier preprocessor rewrote the invocation into JSON before a later one threw.")]
	public async Task When_AnEarlierPreprocessorRewroteIntoJsonAndALaterOneThrows_Then_TheFailureIsReportedAsJson()
	{
		var app = CoreReplApp.Create();
		app.Map("old", () => "ok");
		app.UseArgumentPreprocessor((input, _) => ArgumentPreprocessingResult.Replace([.. input.Arguments, "--json"]));
		app.UseArgumentPreprocessor((_, _) => throw new InvalidOperationException("adapter failed"));

		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);

		(await app.RunAsync(["old"])).Should().Be(1);
		using var document = System.Text.Json.JsonDocument.Parse(writer.ToString());
		document.RootElement.GetRawText().Should().Contain("input_callback_error");
	}

	[TestMethod]
	[DataRow(false, false, DisplayName = "One-shot, arguments")]
	[DataRow(false, true, DisplayName = "One-shot, native command")]
	[DataRow(true, false, DisplayName = "Interactive, arguments")]
	[DataRow(true, true, DisplayName = "Interactive, native command")]
	[Description("Verifies the format of an invocation hook's failure report follows the request as far as the hooks replaced it: an earlier hook asked for JSON, through arguments or a native global, before a later one threw.")]
	public async Task When_AnEarlierHookReplacedTheRequestWithJsonAndALaterOneThrows_Then_TheFailureIsReportedAsJson(bool interactive, bool command)
	{
		var app = CoreReplApp.Create();
		ReplExecutionOutcomeKind? kind = null;
		app.Options(options => options.ExitCodes.Resolver = outcome => { kind = outcome.Kind; return outcome.ExitCode; });
		app.Map("old", () => "ok");
		var replacement = command
			? ReplInvocationRequest.ForCommand("old", globalValues: new Dictionary<string, object?>(StringComparer.Ordinal) { ["output"] = "json" })
			: ReplInvocationRequest.FromArguments(["old", "--json"]);
		app.UseInvocationHook((ReplInvocationContext context) => context.Request.Arguments is ["old"]
			? ReplInvocationResult.Replace(replacement)
			: ReplInvocationResult.Continue());
		app.UseInvocationHook((ReplInvocationContext context) => context.Request.Command is "old" || context.Request.Arguments is ["old", "--json"]
			? throw new InvalidOperationException("hook failed")
			: ReplInvocationResult.Continue());

		using var writer = new StringWriter();
		using var reader = new StringReader(interactive ? "old\nexit\n" : string.Empty);
		using var io = ReplSessionIO.SetSession(writer, reader);

		var exitCode = await app.RunAsync(interactive ? [] : ["old"]);

		writer.ToString().Should().Contain("\"code\": \"input_callback_error\"");
		if (!interactive)
		{
			exitCode.Should().Be(1);
			kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
		}
	}

	[TestMethod]
	[Description("Verifies an input callback failure whose report degrades to unformatted text, the requested transformer failing too, is still the application's failure the caller saw: a handler exception, as any degraded report is.")]
	public async Task When_AnInputCallbackFailureReportDegrades_Then_ItIsAHandlerException()
	{
		var app = CoreReplApp.Create();
		ReplExecutionOutcomeKind? kind = null;
		app.Options(options =>
		{
			options.Output.AddTransformer("broken", new BrokenTransformer());
			options.ExitCodes.Resolver = outcome => { kind = outcome.Kind; return outcome.ExitCode; };
		});
		app.Map("old", () => "ok");
		app.UseArgumentPreprocessor((_, _) => throw new InvalidOperationException("adapter failed"));

		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);

		(await app.RunAsync(["old", "--output:broken"])).Should().Be(1);
		kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
	}

	private sealed class BrokenTransformer : IOutputTransformer
	{
		public string Name => "broken";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("transformer is broken");
	}

	[TestMethod]
	[Description("Verifies an argument preprocessor that throws for an interactive command asking for JSON reports its failure as JSON, as the same command run once does.")]
	public async Task When_AnInputCallbackThrowsForAnInteractiveJsonCommand_Then_TheFailureIsReportedAsJson()
	{
		var app = CoreReplApp.Create();
		app.Map("old", () => "ok");
		app.UseArgumentPreprocessor((input, _) =>
			input.Arguments.Count != 0 && string.Equals(input.Arguments[0], "old", StringComparison.Ordinal)
				? throw new InvalidOperationException("adapter failed")
				: ArgumentPreprocessingResult.Continue());

		using var writer = new StringWriter();
		using var reader = new StringReader("old --json\nexit\n");
		using var io = ReplSessionIO.SetSession(writer, reader);

		_ = await app.RunAsync([]);

		writer.ToString().Should().Contain("\"code\": \"input_callback_error\"");
	}

	[TestMethod]
	[Description("Verifies an input callback failure reported in an unknown output format is the caller's mistake, refused as a usage error, as any other report in that format is.")]
	public async Task When_AnInputCallbackThrowsForAnUnknownFormat_Then_ItIsAUsageError()
	{
		var app = CoreReplApp.Create();
		ReplExecutionOutcomeKind? kind = null;
		app.Options(options => options.ExitCodes.Resolver = outcome => { kind = outcome.Kind; return outcome.ExitCode; });
		app.Map("old", () => "ok");
		app.UseArgumentPreprocessor((_, _) => throw new InvalidOperationException("adapter failed"));

		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);

		(await app.RunAsync(["old", "--output:bogus"])).Should().Be(2);
		kind.Should().Be(ReplExecutionOutcomeKind.UsageError);
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
	[Description("Live analysis and execution must agree on a route whose argument failed: outscored by a route matching more of the literal words, it is an unknown command, and otherwise its argument's error.")]
	[DataRow("tests bad extra", "Unknown command.", DisplayName = "Outscored by another route")]
	[DataRow("tests x", "An argument does not satisfy its route constraint.", DisplayName = "Addressed by the failed route")]
	public async Task When_ARouteArgumentFails_Then_LiveAnalysisRefusesItAsExecutionDoes(string text, string message)
	{
		var app = CoreReplApp.Create();
		app.Map("tests {count:int}", (int count) => count);
		app.Map("tests bad group", () => "ok");

		var analysis = await AnalyzeAsync(app, text);

		analysis.Validation.State.Should().Be(InputValidationState.Invalid);
		analysis.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message).Should().Contain(message);
	}

	[TestMethod]
	[Description("Live analysis weighs every route whose argument failed, as execution does: a route matching more literal words, whose own constraint failed, makes the input invalid rather than an unknown command.")]
	public async Task When_TheRankedFailureIsOutscoredByAnotherFailedRoute_Then_LiveAnalysisReportsTheArgument()
	{
		var app = CoreReplApp.Create();
		app.Map("cmd {a:int} {b:int} {c:int} {d:int} {e:int} {bad:int}", (int a, int b, int c, int d, int e, int bad) => bad);
		app.Map("cmd 1 {bad:alpha}", (string bad) => bad);

		var analysis = await AnalyzeAsync(app, "cmd 1 2 3 4 5 x");

		analysis.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)
			.Should().Contain("An argument does not satisfy its route constraint.");
	}

	[TestMethod]
	[Description("Live analysis keeps a line incomplete while a route accepting every word typed waits for more, even when an overload rejects one of those words on a line too short for it.")]
	public async Task When_AnOverloadRejectsAWordAnotherRouteAccepts_Then_LiveAnalysisStaysIncomplete()
	{
		var app = CoreReplApp.Create();
		app.Map("deploy {version:int} {target}", (int version, string target) => target);
		app.Map("deploy {name} {target}", (string name, string target) => target);

		var analysis = await AnalyzeAsync(app, "deploy foo");

		analysis.Validation.State.Should().Be(InputValidationState.Incomplete);
	}

	[TestMethod]
	[Description("Live analysis reports a value an addressed route rejects even before the line is complete, as execution refuses it: no more words can make that value valid.")]
	public async Task When_AnIncompleteLineHasARejectedValue_Then_LiveAnalysisReportsTheArgument()
	{
		var app = CoreReplApp.Create();
		app.Map("x {id:int} {name}", (int id, string name) => name);

		var analysis = await AnalyzeAsync(app, "x y");

		analysis.Validation.State.Should().Be(InputValidationState.Invalid);
		analysis.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)
			.Should().Contain("An argument does not satisfy its route constraint.");
	}

	[TestMethod]
	[Description("Live analysis inside a context must not blame a route rejecting a word of the scope, which the context accepted and the line does not contain: the line is an unknown command there, as execution refuses it.")]
	public async Task When_ARouteRejectsAScopeWord_Then_LiveAnalysisDoesNotBlameIt()
	{
		var app = CoreReplApp.Create();
		app.Map("project {id:int}", (int id) => id);
		app.Context("project {name}", project => project.Map("build", () => "built"));
		const string text = "bogus";

		var input = await app.PreprocessInputAsync([text], ["project", "foo"], InputProcessingMode.Preview, text, text.Length,
			CancellationToken.None, isInteractive: true);
		var analysis = await app.AnalyzeInputAsync(input, app.CurrentServiceProvider, CancellationToken.None);

		analysis.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)
			.Should().Contain("Unknown command.").And.NotContain("An argument does not satisfy its route constraint.");
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
	[DataRow("invalid", "Tools are locked.", DisplayName = "Invalid")]
	[DataRow("incomplete", "Tools need a reason.", DisplayName = "Incomplete")]
	[DataRow("throws", "tools validator failure", DisplayName = "Validator throws")]
	[DataRow("valid", null, DisplayName = "Valid")]
	[Description("Verifies an interactive submission naming a context is checked by the application's input validators, once, before the scope changes: a refusal or a validator failure is reported, leaves the session at the root and skips the context validator, while a valid result runs the context validator and enters the context.")]
	public async Task When_ASubmissionNamesAContext_Then_InputValidationDecidesTheNavigation(string verdict, string? reported)
	{
		var app = CoreReplApp.Create();
		var navigationChecks = 0;
		app.UseInputValidator((ParsedInputContext input) => CheckToolsNavigation(input, verdict, ref navigationChecks));
		var contextChecks = 0;
		string? ran = null;
		app.Map("where", () => { ran = "root"; });
		app.Context("tools", scope => scope.Map("where", () => { ran = "tools"; }), validation: () => ++contextChecks > 0);
		using var writer = new StringWriter();
		using var reader = new StringReader("tools\nwhere\nexit\n");
		using var io = ReplSessionIO.SetSession(writer, reader);

		await app.RunAsync([]);

		var valid = string.Equals(verdict, "valid", StringComparison.Ordinal);
		navigationChecks.Should().Be(1, writer.ToString());
		// A valid navigation runs the context validator, which then guards each command in the context as well.
		if (valid)
		{
			contextChecks.Should().BePositive(writer.ToString());
		}
		else
		{
			contextChecks.Should().Be(0, writer.ToString());
		}

		ran.Should().Be(valid ? "tools" : "root", writer.ToString());
		if (reported is not null)
		{
			writer.ToString().Should().Contain(reported);
		}
	}

	[TestMethod]
	[Description("Verifies a command line naming a context is checked by the application's input validators before the context is entered: an invalid result is reported and the context validator never runs.")]
	public async Task When_ACommandLineNamesAContextTheValidatorsRefuse_Then_TheContextIsNotEntered()
	{
		var app = CoreReplApp.Create();
		var navigationChecks = 0;
		app.UseInputValidator((ParsedInputContext input) => CheckToolsNavigation(input, "invalid", ref navigationChecks));
		var contextChecks = 0;
		app.Context("tools", scope => scope.Map("where", () => "tools"), validation: () => ++contextChecks > 0);
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);

		var exitCode = await app.RunAsync(["tools"]);

		exitCode.Should().NotBe(0, writer.ToString());
		navigationChecks.Should().Be(1);
		contextChecks.Should().Be(0);
		writer.ToString().Should().Contain("Tools are locked.");
	}

	// Counts the execution-time checks of a submission naming the tools context, answering them with the verdict.
	private static InputValidationResult CheckToolsNavigation(ParsedInputContext input, string verdict, ref int checks)
	{
		if (input.Input.Mode != InputProcessingMode.Execution
			|| !input.Input.Arguments.SequenceEqual(["tools"], StringComparer.Ordinal))
		{
			return InputValidationResult.Valid();
		}

		checks++;
		return verdict switch
		{
			"invalid" => InputValidationResult.Invalid("Tools are locked."),
			"incomplete" => InputValidationResult.Incomplete("Tools need a reason."),
			"throws" => throw new InvalidOperationException("tools validator failure"),
			_ => InputValidationResult.Valid(),
		};
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
