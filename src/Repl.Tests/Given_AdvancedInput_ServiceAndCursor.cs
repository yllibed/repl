using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Repl.Tests;

[TestClass]
public sealed class Given_AdvancedInput_ServiceAndCursor
{
	private static readonly ShellKind[] SupportedShells = [ShellKind.Bash, ShellKind.Zsh, ShellKind.PowerShell, ShellKind.Fish, ShellKind.Nu];

	[TestMethod]
	[DataRow("app", 0, false, DisplayName = "Cursor before the executable")]
	[DataRow("app ", 1, false, DisplayName = "Cursor inside the executable, before a space")]
	[DataRow("app q", 1, false, DisplayName = "Cursor inside the executable, before a partial command")]
	[DataRow("app", 1, false, DisplayName = "Cursor inside a bare executable")]
	[DataRow("app", 3, false, DisplayName = "Cursor at the end of a bare executable")]
	[DataRow("app ", 3, false, DisplayName = "Cursor at the end of the executable, before a space")]
	[DataRow("  app q", 1, false, DisplayName = "Cursor in leading whitespace")]
	[DataRow("app ", 4, true, DisplayName = "Cursor at the argument boundary")]
	[DataRow("app  q", 4, true, DisplayName = "Cursor between two separating spaces")]
	[DataRow("app q", 5, true, DisplayName = "Cursor after a partial command")]
	[Description("Verifies advanced input leaves shell completion unchanged around the executable token: with an identity adapter or an always-valid validator, every supported shell offers what plain completion offers, nothing while the cursor is on the executable and the command once it is past it.")]
	public async Task When_AdvancedInputIsEnabled_Then_CompletionAroundTheExecutableMatchesPlainCompletion(
		string line, int cursor, bool offersCommand)
	{
		foreach (var shell in SupportedShells)
		{
			var plain = await CompleteAsync(CreateQueryApp(static _ => { }), line, cursor, shell).ConfigureAwait(false);
			if (offersCommand)
			{
				plain.Should().Contain("query", $"plain completion in {shell}");
			}
			else
			{
				plain.Should().BeEmpty($"plain completion in {shell}");
			}

			var identityAdapter = await CompleteAsync(CreateQueryApp(static app =>
				app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Continue()))), line, cursor, shell)
				.ConfigureAwait(false);
			identityAdapter.Should().Equal(plain, $"an identity adapter in {shell}");
			var validValidator = await CompleteAsync(CreateQueryApp(static app =>
				app.UseInputValidator(InputValidationResult (ParsedInputContext _) => InputValidationResult.Valid())), line, cursor, shell)
				.ConfigureAwait(false);
			validValidator.Should().Equal(plain, $"an always-valid validator in {shell}");
		}
	}

	[TestMethod]
	[DataRow("app ", 4, DisplayName = "Cursor at the argument boundary")]
	[DataRow("app  q", 4, DisplayName = "Cursor between two separating spaces")]
	[DataRow("app  q", 5, DisplayName = "Cursor at a partial command after two spaces")]
	[Description("Verifies a cursor past the executable, even in the spaces before the arguments, goes through input validation: an always-invalid validator leaves nothing to complete.")]
	public async Task When_InputValidationRefusesTheArguments_Then_NothingPastTheExecutableIsCompleted(string line, int cursor)
	{
		foreach (var shell in SupportedShells)
		{
			var refused = await CompleteAsync(CreateQueryApp(static app =>
				app.UseInputValidator(InputValidationResult (ParsedInputContext _) => InputValidationResult.Invalid("Refused."))), line, cursor, shell)
				.ConfigureAwait(false);
			refused.Should().BeEmpty($"an always-invalid validator in {shell}");
		}
	}

	[TestMethod]
	[DataRow("app", 3, DisplayName = "Cursor at the end of a bare executable")]
	[DataRow("app q", 1, DisplayName = "Cursor inside the executable")]
	[Description("Verifies a cursor on the executable is answered as plain completion without running input callbacks: a failing adapter is never invoked, so nothing is offered and no callback diagnostic is written.")]
	public async Task When_TheCursorIsOnTheExecutable_Then_InputCallbacksDoNotRun(string line, int cursor)
	{
		var calls = 0;
		var app = CreateQueryApp(app => app.UseArgumentPreprocessor((_, _) =>
		{
			calls++;
			throw new InvalidOperationException("adapter failure");
		}));
		using var error = new StringWriter();
		using var io = ReplSessionIO.SetSession(TextWriter.Null, TextReader.Null, error: error);

		var candidates = await CompleteAsync(app, line, cursor, ShellKind.Bash).ConfigureAwait(false);

		candidates.Should().BeEmpty();
		calls.Should().Be(0);
		error.ToString().Should().BeEmpty();
	}

	private static CoreReplApp CreateQueryApp(Action<CoreReplApp> configure)
	{
		var app = CoreReplApp.Create();
		app.Map("query", () => "ok");
		configure(app);
		return app;
	}

	private static Task<string[]> CompleteAsync(CoreReplApp app, string line, int cursor, ShellKind shell) =>
		new ShellCompletionEngine(app)
			.ResolveShellCompletionCandidatesAsync(line, cursor, shell, app.CurrentServiceProvider, CancellationToken.None)
			.AsTask();

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("An omitted required value cannot preview as valid merely because a potentially null service is registered.")]
	public async Task When_ARequiredServiceFallbackIsOmitted_Then_PreviewDefersTheDecision(bool collection)
	{
		var calls = 0;
		var app = ReplApp.Create(services => services.AddTransient(collection ? typeof(int[]) : typeof(int), _ => { calls++; return null!; }));
		if (collection)
		{
			app.Map("query", ([ReplOption(Arity = ReplArity.OneOrMore)] int[] counts) => counts.Length);
		}
		else { app.Map("query", (int count) => count); }
		app.UseInputValidator((ParsedInputContext _) => InputValidationResult.Valid());
		var processed = await app.Core.PreprocessInputAsync(["query"], [], InputProcessingMode.Preview, "query", 5, CancellationToken.None);
		(await app.Core.AnalyzeInputAsync(processed, app.Services, CancellationToken.None)).Validation.State.Should().Be(InputValidationState.Incomplete);
		calls.Should().Be(0);
		(await app.RunAsync(["query"])).Should().Be(2);
		calls.Should().Be(1);
	}

	[TestMethod]
	[Description("An optional literal default remains usable when an omitted parameter's service factory returns null.")]
	public async Task When_ANullServiceHasALiteralDefault_Then_OmissionUsesTheDefault()
	{
		var calls = 0;
		var app = ReplApp.Create(services => services.AddTransient<string>(_ => { calls++; return null!; }));
		string? observed = null;
		app.Map("query", (string prefix = "default") => { observed = prefix; });
		app.UseInputValidator((ParsedInputContext input) =>
		{
			if (input.Input.Mode == InputProcessingMode.Execution)
			{
				input.TryGetValue<string>("prefix", out var prefix).Should().BeTrue();
				prefix.Should().Be("default");
			}
			return InputValidationResult.Valid();
		});
		var processed = await app.Core.PreprocessInputAsync(["query"], [], InputProcessingMode.Preview, "query", 5, CancellationToken.None);
		(await app.Core.AnalyzeInputAsync(processed, app.Services, CancellationToken.None)).Validation.State.Should().Be(InputValidationState.Valid);
		calls.Should().Be(0);
		(await app.RunAsync(["query"])).Should().Be(0);
		observed.Should().Be("default");
		calls.Should().Be(1);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Preview must defer uncertain positional assignments without activating factories or rejecting an unrelated named value.")]
	public async Task When_PreviewCannotKnowTheServiceValue_Then_DependentPositionalsAreDeferred(bool namedLimit)
	{
		var calls = 0;
		var app = ReplApp.Create(services => services.AddTransient<string>(_ => { calls++; return null!; }));
		app.Map("query", (string prefix, int limit) => limit);
		string[] args = namedLimit ? ["query", "foo", "--limit", "3"] : ["query", "foo", "3"];
		var text = string.Join(' ', args);
		var processed = await app.Core.PreprocessInputAsync(args, [], InputProcessingMode.Preview, text, text.Length, CancellationToken.None);
		var analysis = await app.Core.AnalyzeInputAsync(processed, app.Services, CancellationToken.None);
		analysis.Validation.State.Should().Be(InputValidationState.Incomplete);
		analysis.TryGetValue<string>("prefix", out _).Should().BeFalse();
		analysis.TryGetValue<int>("limit", out var limit).Should().Be(namedLimit);
		if (namedLimit) { limit.Should().Be(3); }
		calls.Should().Be(0);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Submission and binding must share a transient fallback result, while named input bypasses the factory.")]
	public async Task When_AServiceReturnsAValue_Then_ValidationAndBindingUseOneResolution(bool namedPrefix)
	{
		var calls = 0;
		var app = ReplApp.Create(services => services.AddTransient<string>(_ => "service" + ++calls));
		string? observed = null;
		app.Map("query", (string prefix, int limit) => { observed = prefix; limit.Should().Be(3); });
		app.UseInputValidator((ParsedInputContext input) =>
		{
			input.TryGetValue<int>("limit", out var limit).Should().BeTrue();
			limit.Should().Be(3);
			input.TryGetValue<string>("prefix", out _).Should().Be(namedPrefix);
			return InputValidationResult.Valid();
		});
		string[] args = namedPrefix ? ["query", "--prefix", "named", "--limit", "3"] : ["query", "3"];
		(await app.RunAsync(args)).Should().Be(0);
		observed.Should().Be(namedPrefix ? "named" : "service1");
		calls.Should().Be(namedPrefix ? 0 : 1);
	}

	[TestMethod]
	[Description("A null collection service must leave its positional values available for both validation and binding.")]
	public async Task When_ACollectionServiceReturnsNull_Then_PositionalElementsRemainAvailable()
	{
		var calls = 0;
		var app = ReplApp.Create(services => services.AddTransient<string[]>(_ => { calls++; return null!; }));
		string[]? observed = null;
		app.Map("query", (string[] terms) => { observed = terms; });
		app.UseInputValidator((ParsedInputContext input) =>
		{
			input.TryGetValue<string[]>("terms", out var terms).Should().BeTrue();
			terms.Should().Equal("one", "two");
			return InputValidationResult.Valid();
		});
		(await app.RunAsync(["query", "one", "two"])).Should().Be(0);
		observed.Should().Equal("one", "two");
		calls.Should().Be(1);
	}

	[TestMethod]
	[DataRow(false, false, false)]
	[DataRow(true, false, false)]
	[DataRow(false, true, false)]
	[DataRow(true, true, false)]
	[DataRow(false, false, true)]
	[DataRow(true, false, true)]
	[DataRow(false, true, true)]
	[DataRow(true, true, true)]
	[Description("Enabling validation must preserve the existing format, binding outcome and exit mapping of a failing service factory.")]
	public async Task When_AnImplicitFactoryThrows_Then_InputValidationDoesNotReclassifyIt(bool json, bool unrelatedCancellation, bool customCodes)
	{
		foreach (var validate in new[] { false, true })
		{
			var calls = 0;
			var app = ReplApp.Create(services => services.AddTransient<string>(_ =>
			{
				calls++;
				throw unrelatedCancellation ? new OperationCanceledException("factory failed") : new InvalidOperationException("factory failed");
			}));
			ReplExecutionOutcomeKind? kind = null;
			app.Options(options =>
			{
				if (customCodes) { options.ExitCodes.BindingError = 42; options.ExitCodes.HandlerException = 43; }
				options.ExitCodes.Resolver = outcome => { kind = outcome.Kind; return outcome.ExitCode; };
			});
			var validated = false;
			app.Map("query", (string prefix, int limit) => limit);
			if (validate) { app.UseInputValidator((ParsedInputContext _) => { validated = true; return InputValidationResult.Valid(); }); }
			using var output = new StringWriter();
			using var io = ReplSessionIO.SetSession(output, TextReader.Null);
			string[] args = json ? ["query", "3", "--output:json"] : ["query", "3"];
			(await app.RunAsync(args)).Should().Be(customCodes ? 42 : 2);
			kind.Should().Be(ReplExecutionOutcomeKind.BindingError);
			calls.Should().Be(1);
			validated.Should().BeFalse();
			output.ToString().Should().Contain("factory failed").And.NotContain("input_callback_error");
			if (json)
			{
				using var document = JsonDocument.Parse(output.ToString());
				document.RootElement.ToString().Should().Contain("execution_error");
			}
		}
	}

	[TestMethod]
	[DataRow("foo")]
	[DataRow("1")]
	[Description("A null-returning service factory must leave positional binding unchanged when a no-op validator is enabled.")]
	public async Task When_AServiceFactoryReturnsNull_Then_ValidationUsesThePositionalFallback(string prefix)
	{
		foreach (var validate in new[] { false, true })
		{
			var calls = 0;
			var app = ReplApp.Create(services => services.AddTransient<string>(_ => { calls++; return null!; }));
			(string Prefix, int Limit)? observed = null;
			app.Map("query", (string prefix, int limit) => { observed = (prefix, limit); });
			if (validate)
			{
				app.UseInputValidator((ParsedInputContext input) =>
				{
					input.TryGetValue<string>("prefix", out var value).Should().BeTrue();
					value.Should().Be(prefix);
					input.TryGetValue<int>("limit", out var limit).Should().BeTrue();
					limit.Should().Be(3);
					return InputValidationResult.Valid();
				});
			}
			(await app.RunAsync(["query", prefix, "3"])).Should().Be(0);
			observed.Should().Be((prefix, 3));
			calls.Should().Be(1);
		}
	}

	[TestMethod]
	[DataRow(ShellKind.Bash)]
	[DataRow(ShellKind.Zsh)]
	[DataRow(ShellKind.PowerShell)]
	[DataRow(ShellKind.Fish)]
	[DataRow(ShellKind.Nu)]
	[Description("A surviving token's origin must not authorize completion of a different token removed by the adapter.")]
	public async Task When_TheCurrentShellTokenWasRemoved_Then_TheProviderIsNotInvoked(ShellKind shell)
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((input, _) => ValueTask.FromResult(
			ArgumentPreprocessingResult.Replace(["query", input.Arguments[1]])));
		var calls = 0;
		app.Map("query {text}", (string text) => text).WithCompletion("text", (_, _, _) =>
		{
			calls++;
			return ValueTask.FromResult<IReadOnlyList<string>>(["plain"]);
		}, CompletionProviderScope.InteractiveAndShell);
		var engine = new ShellCompletionEngine(app);
		var line = "app /q p removed";
		foreach (var cursor in new[] { line.Length, line.Length - 2 })
		{
			(await engine.ResolveShellCompletionCandidatesAsync(line, cursor, shell, app.CurrentServiceProvider,
				CancellationToken.None)).Should().BeEmpty();
		}
		calls.Should().Be(0);
		(await engine.ResolveShellCompletionCandidatesAsync(line, "app /q p".Length, shell, app.CurrentServiceProvider,
			CancellationToken.None)).Should().Contain("plain");
		calls.Should().Be(1);
	}
}
