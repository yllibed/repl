using Microsoft.Extensions.DependencyInjection;

namespace Repl.Tests;

[TestClass]
public sealed class Given_AdvancedInput_EditorBoundaries
{
	[TestMethod]
	[DataRow(12)]
	[DataRow(10)]
	[Description("Completion must not invoke a provider or edit a surviving argument when the original cursor belongs to a removed token.")]
	public async Task When_TheEditorCursorCannotBeMapped_Then_NoProjectedEditIsOffered(int cursor)
	{
		var app = CoreReplApp.Create();
		app.Options(options => options.Interactive.Autocomplete.NormalizeInputOnTab = false);
		app.UseArgumentPreprocessor((input, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Replace(["query", input.Arguments[1]])));
		var calls = 0;
		app.Map("query {text}", (string text) => text).WithCompletion("text", (_, _, _) =>
		{
			calls++;
			return ValueTask.FromResult<IReadOnlyList<string>>(["plain"]);
		});
		var result = await app.Autocomplete.ResolveAutocompleteAsync(new("/q p removed", cursor, MenuRequested: false, ExplicitCompletion: true),
			[], app.CurrentServiceProvider, CancellationToken.None);
		result!.Value.Suggestions.Should().BeEmpty();
		result.Value.ReplaceStart.Should().Be(cursor);
		result.Value.ReplaceLength.Should().Be(0);
		result.Value.NormalizedInput.Should().BeNull();
		calls.Should().Be(0);
		var mapped = await app.Autocomplete.ResolveAutocompleteAsync(new("/q p removed", 4, MenuRequested: false, ExplicitCompletion: true),
			[], app.CurrentServiceProvider, CancellationToken.None);
		mapped!.Value.Suggestions.Select(item => item.Value).Should().Contain("plain");
		mapped.Value.ReplaceStart.Should().Be(3);
		calls.Should().Be(1);
	}

	[TestMethod]
	[DataRow(false, false)]
	[DataRow(true, false)]
	[DataRow(false, true)]
	[DataRow(true, true)]
	[Description("Handled editor input must stop before validator services and completion providers while retaining adapter diagnostics.")]
	public async Task When_AnAdapterHandlesEditorInput_Then_ValidationDoesNotRun(bool completion, bool diagnostic)
	{
		var created = 0;
		var app = ReplApp.Create(services => services.AddTransient(_ => { created++; return new ValidatorDependency(); }));
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Replace(["query", "p"])));
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(
			ArgumentPreprocessingResult.Handled(validation: diagnostic ? InputValidationResult.Incomplete("host action is handled") : null)));
		app.UseInputValidator(InputValidationResult (ParsedInputContext _, ValidatorDependency dependency) => throw new InvalidOperationException("must not validate"));
		var providerCalls = 0;
		app.Map("query {text}", (string text) => text).WithCompletion("text", (_, _, _) =>
		{
			providerCalls++;
			return ValueTask.FromResult<IReadOnlyList<string>>(["plain"]);
		});
		var result = await app.Core.Autocomplete.ResolveAutocompleteAsync(new("/external", 9, MenuRequested: false, ExplicitCompletion: completion),
			[], app.Services, CancellationToken.None);
		if (diagnostic) { result!.Value.HintLine.Should().Contain("host action is handled").And.NotContain("failed"); }
		else { result!.Value.HintLine.Should().BeNull(); }
		result.Value.Suggestions.Should().BeEmpty();
		result.Value.NormalizedInput.Should().BeNull();
		created.Should().Be(0);
		providerCalls.Should().Be(0);
	}

	private sealed class ValidatorDependency;

	[TestMethod]
	[Description("A failure while resolving a validator dependency must remain a callback failure, distinct from handler fallback resolution.")]
	public async Task When_AValidatorServiceFactoryFails_Then_ItRetainsTheCallbackOutcome()
	{
		var app = ReplApp.Create(services => services.AddTransient<ValidatorDependency>(_ => throw new InvalidOperationException("validator service failed")));
		ReplExecutionOutcomeKind? kind = null;
		app.Options(options => options.ExitCodes.Resolver = outcome => { kind = outcome.Kind; return outcome.ExitCode; });
		app.Map("query", (int limit) => limit);
		app.UseInputValidator((ParsedInputContext _, ValidatorDependency dependency) => InputValidationResult.Valid());
		(await app.RunAsync(["query", "3"])).Should().Be(1);
		kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Actual caller cancellation during fallback resolution must retain its cancellation outcome with and without validators.")]
	public async Task When_TheCallerCancelsDuringResolution_Then_TheCancellationOutcomeIsPreserved(bool validate)
	{
		using var cancellation = new CancellationTokenSource();
		var app = ReplApp.Create(services => services.AddTransient<string>(_ =>
		{
			cancellation.Cancel();
			throw new OperationCanceledException(cancellation.Token);
		}));
		ReplExecutionOutcomeKind? kind = null;
		app.Options(options =>
		{
			options.ExitCodes.Cancelled = 130;
			options.ExitCodes.Resolver = outcome => { kind = outcome.Kind; return outcome.ExitCode; };
		});
		app.Map("query", (string prefix, int limit) => limit);
		if (validate) { app.UseInputValidator((ParsedInputContext _) => InputValidationResult.Valid()); }
		(await app.RunAsync(["query", "3"], cancellation.Token)).Should().Be(130);
		kind.Should().Be(ReplExecutionOutcomeKind.Cancelled);
	}
}
