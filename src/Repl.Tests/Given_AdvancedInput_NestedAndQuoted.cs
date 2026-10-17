using Microsoft.Extensions.DependencyInjection;

namespace Repl.Tests;

[TestClass]
public sealed class Given_AdvancedInput_NestedAndQuoted
{
	[TestMethod]
	[DataRow(ShellKind.Bash)]
	[DataRow(ShellKind.Zsh)]
	[DataRow(ShellKind.PowerShell)]
	[DataRow(ShellKind.Fish)]
	[DataRow(ShellKind.Nu)]
	[Description("Normalizing legacy syntax must not remove the original shell quote context and expose interpolated provider values.")]
	public async Task When_AdaptedShellInputContainsQuotes_Then_ProviderValuesAreSuppressed(ShellKind shell)
	{
		var app = CreateLegacyApp();
		var providerCalls = 0;
		app.Map("query {text}", (string text) => text).WithCompletion("text", (_, _, _) =>
		{
			providerCalls++;
			return ValueTask.FromResult<IReadOnlyList<string>>(["plain", "p$(printf PROBE)", "p$PR118_PROBE"]);
		}, CompletionProviderScope.InteractiveAndShell);
		var engine = new ShellCompletionEngine(app);
		foreach (var suffix in new[] { "\"p", "'p", "\"p\\\"", "\\p" })
		{
			var line = "app /q " + suffix;
			var candidates = await engine.ResolveShellCompletionCandidatesAsync(line, line.Length, shell, app.CurrentServiceProvider, CancellationToken.None);
			candidates.Should().BeEmpty();
		}
		providerCalls.Should().Be(0);
		var unquoted = "app /q p";
		(await engine.ResolveShellCompletionCandidatesAsync(unquoted, unquoted.Length, shell, app.CurrentServiceProvider, CancellationToken.None)).Should().Contain("plain");
	}

	[TestMethod]
	[Description("A no-op validator must not consume a DI-backed parameter as positional input or construct it during preview.")]
	public async Task When_AServicePrecedesAnInput_Then_AnalysisPreservesThePositionalValue()
	{
		var created = 0;
		var app = ReplApp.Create(services => services.AddTransient(_ => { created++; return new SearchService(); }));
		app.Map("query", (SearchService search, int limit) => limit);
		app.UseInputValidator((ParsedInputContext input) =>
		{
			if (input.Input.Mode != InputProcessingMode.Execution)
			{
				input.TryGetValue<int>("limit", out _).Should().BeFalse();
				created.Should().Be(0);
				return InputValidationResult.Valid();
			}
			input.TryGetValue<int>("limit", out var limit).Should().BeTrue();
			limit.Should().Be(3);
			created.Should().Be(1);
			return InputValidationResult.Valid();
		});
		var processed = await app.Core.PreprocessInputAsync(["query", "3"], [], InputProcessingMode.Preview, "query 3", 7, CancellationToken.None);
		(await app.Core.AnalyzeInputAsync(processed, app.Services, CancellationToken.None)).Validation.State.Should().Be(InputValidationState.Incomplete);
		created.Should().Be(0);
		(await app.RunAsync(["query", "3"])).Should().Be(0);
		created.Should().Be(1);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("A registered scalar fallback may supply an omitted input without preview activating the registration factory.")]
	public async Task When_AScalarHasAServiceFallback_Then_OmissionRemainsValid(bool namedOverride)
	{
		var services = new ServiceCollection();
		var created = 0;
		services.AddSingleton(typeof(int), _ => { created++; return 42; });
		var app = ReplApp.Create(collection => { foreach (var registration in services) { collection.Add(registration); } });
		var countSeen = 0;
		app.Map("query", (int count) => { countSeen = count; });
		app.UseInputValidator((ParsedInputContext _) => InputValidationResult.Valid());
		string[] arguments = namedOverride ? ["query", "--count", "7"] : ["query"];
		var text = string.Join(' ', arguments);
		var processed = await app.Core.PreprocessInputAsync(arguments, [], InputProcessingMode.Preview, text, text.Length, CancellationToken.None);
		(await app.Core.AnalyzeInputAsync(processed, app.Services, CancellationToken.None)).Validation.State.Should()
			.Be(namedOverride ? InputValidationState.Valid : InputValidationState.Incomplete);
		created.Should().Be(0);
		(await app.RunAsync(arguments)).Should().Be(0);
		countSeen.Should().Be(namedOverride ? 7 : 42);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("A nested invocation must retain its globals through both validation and handler execution, then restore the parent analysis frame.")]
	public async Task When_AValidatorInvokesAnotherCommand_Then_ChildGlobalsRemainInEffectThroughExecution(bool childFails)
	{
		var app = ReplApp.Create();
		app.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		var seen = new List<string?>();
		app.Map("outer", (IGlobalOptionsAccessor globals) => { seen.Add(globals.GetValue<string>("tenant")); });
		app.Map("inner", (IGlobalOptionsAccessor globals) =>
		{
			seen.Add(globals.GetValue<string>("tenant"));
			if (childFails) { throw new InvalidOperationException("child failed"); }
		});
		app.UseInputValidator(async (ParsedInputContext input, IGlobalOptionsAccessor globals, IServiceProvider services) =>
		{
			seen.Add(globals.GetValue<string>("tenant"));
			if (input.Command is "outer")
			{
				(await app.Core.RunSubInvocationAsync(["inner", "--tenant", "inner"], services).ConfigureAwait(false)).Should().Be(childFails ? 1 : 0);
				seen.Add(globals.GetValue<string>("tenant"));
			}
			return InputValidationResult.Valid();
		});
		(await app.RunAsync(["outer", "--tenant", "outer"])).Should().Be(0);
		seen.Should().Equal("outer", "inner", "inner", "outer", "outer");
	}

	[TestMethod]
	[Description("A structured child must bypass textual input validation even when its caller has an active processed-input frame.")]
	public async Task When_AHandlerInvokesAStructuredChild_Then_ParentInputIsNotInherited()
	{
		var app = ReplApp.Create();
		var validations = new List<string?>();
		var childRan = false;
		app.UseInputValidator((ParsedInputContext input) =>
		{
			validations.Add(input.Command);
			return input.Command is "inner" ? InputValidationResult.Invalid("structured validation must not run") : InputValidationResult.Valid();
		});
		app.Map("inner", () => { childRan = true; });
		app.Map("outer", async (IServiceProvider services) =>
		{
			var previous = ReplSessionIO.IsProgrammatic;
			try
			{
				ReplSessionIO.IsProgrammatic = true;
				using var contract = ReplSessionIO.PushProgrammaticInvocationContract(adapterContractVersion: 1);
				(await app.Core.RunSubInvocationAsync(["inner"], services).ConfigureAwait(false)).Should().Be(0);
			}
			finally { ReplSessionIO.IsProgrammatic = previous; }
		});
		(await app.RunAsync(["outer"])).Should().Be(0);
		childRan.Should().BeTrue();
		validations.Should().Equal("outer");
	}

	[TestMethod]
	[DataRow("/q \"hello\"", 5, 7)]
	[DataRow("/q 'hello world'", 5, 8)]
	[DataRow("/q 'hello world'", 15, 18)]
	[DataRow("/q 'hello world'", 16, 19)]
	[Description("Cursor normalization must preserve the decoded insertion point rather than a raw offset containing quote delimiters.")]
	public async Task When_AQuotedArgumentIsNormalized_Then_TheCursorKeepsItsLogicalPosition(string text, int cursor, int expected)
	{
		var app = CreateLegacyApp();
		app.Map("query {text}", (string text) => text);
		var assist = await app.Autocomplete.ResolveAutocompleteAsync(new(text, cursor, MenuRequested: false, ExplicitCompletion: true),
			[], app.CurrentServiceProvider, CancellationToken.None);
		assist!.Value.NormalizedCursor.Should().Be(expected);
	}

	[TestMethod]
	[Description("An interactive command needs its own execution-global frame when a validator awaits a nested command with different globals.")]
	public async Task When_AnInteractiveValidatorInvokesAChild_Then_TheHandlerStillSeesItsOwnGlobals()
	{
		var app = ReplApp.Create();
		app.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		string? observed = null;
		app.Map("outer", (IGlobalOptionsAccessor globals) => { observed = globals.GetValue<string>("tenant"); });
		app.Map("inner", () => "child complete");
		app.UseInputValidator(async (ParsedInputContext input, IServiceProvider services) =>
		{
			if (input.Command is "outer")
			{
				(await app.Core.RunSubInvocationAsync(["inner", "--tenant", "inner"], services).ConfigureAwait(false)).Should().Be(0);
			}
			return InputValidationResult.Valid();
		});
		using var output = new StringWriter();
		using var reader = new StringReader("outer --tenant outer\nexit\n");
		using var io = ReplSessionIO.SetSession(output, reader);
		(await app.RunAsync([])).Should().Be(0);
		observed.Should().Be("outer");
	}

	[TestMethod]
	[Description("Host cancellation during preprocessing must propagate rather than being rendered as an unrelated callback failure or a recoverable command interruption.")]
	public async Task When_TheSessionCancelsDuringPreprocessing_Then_CancellationPropagates()
	{
		var app = CoreReplApp.Create();
		using var cancellation = new CancellationTokenSource();
		app.UseArgumentPreprocessor(async (input, token) =>
		{
			if (input.Arguments is ["boom"])
			{
				await cancellation.CancelAsync().ConfigureAwait(false);
				token.ThrowIfCancellationRequested();
			}
			return ArgumentPreprocessingResult.Continue();
		});
		using var output = new StringWriter();
		using var reader = new StringReader("boom\nexit\n");
		using var io = ReplSessionIO.SetSession(output, reader);
		Func<Task> run = () => app.RunAsync([], cancellation.Token).AsTask();
		await run.Should().ThrowAsync<OperationCanceledException>();
		output.ToString().Should().NotContain("Error:").And.NotContain("Cancelled.");
	}

	[TestMethod]
	[Description("An adapted shell value with no original source mapping must not receive a replacement intended for a different token.")]
	public async Task When_TheOriginalShellTokenCannotBeMapped_Then_NoSuggestionIsInserted()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Replace(["query", "p"])));
		app.Map("query {text}", (string text) => text).WithCompletion("text",
			(_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["plain"]), CompletionProviderScope.InteractiveAndShell);
		var line = "app /q original";
		(await new ShellCompletionEngine(app).ResolveShellCompletionCandidatesAsync(line, line.Length, ShellKind.Bash,
			app.CurrentServiceProvider, CancellationToken.None)).Should().BeEmpty();
	}

	[TestMethod]
	[DataRow(ShellKind.Bash)]
	[DataRow(ShellKind.Zsh)]
	[DataRow(ShellKind.PowerShell)]
	[DataRow(ShellKind.Fish)]
	[DataRow(ShellKind.Nu)]
	[Description("Shell escapes can make quote context cross the tokenizer's word boundary, so safety must inspect the full original prefix rather than its last token.")]
	public async Task When_AnEscapedQuotePrecedesWhitespace_Then_AdaptedShellValuesAreSuppressed(ShellKind shell)
	{
		var app = CreateLegacyApp();
		var calls = 0;
		app.Map("query {first} {second}", (string first, string second) => second).WithCompletion("second", (_, _, _) =>
		{
			calls++;
			return ValueTask.FromResult<IReadOnlyList<string>>(["plain", "p$(printf PROBE)"]);
		}, CompletionProviderScope.InteractiveAndShell);
		var engine = new ShellCompletionEngine(app);
		foreach (var arguments in new[] { "\"foo\\\" p", "\"foo`\" p", "\"foo\" p" })
		{
			var line = "app /q " + arguments;
			(await engine.ResolveShellCompletionCandidatesAsync(line, line.Length, shell, app.CurrentServiceProvider, CancellationToken.None)).Should().BeEmpty();
		}
		calls.Should().Be(0);
		var unquoted = "app /q first p";
		(await engine.ResolveShellCompletionCandidatesAsync(unquoted, unquoted.Length, shell, app.CurrentServiceProvider, CancellationToken.None)).Should().Contain("plain");
	}

	private static CoreReplApp CreateLegacyApp()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((input, _) => ValueTask.FromResult(input.Arguments.Count != 0 && input.Arguments[0] is "/q"
			? ArgumentPreprocessingResult.Replace(["query", .. input.Arguments.Skip(1)]) : ArgumentPreprocessingResult.Continue()));
		return app;
	}

	private sealed class SearchService;
}
