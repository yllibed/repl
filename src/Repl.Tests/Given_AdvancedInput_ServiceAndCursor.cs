using Microsoft.Extensions.DependencyInjection;

namespace Repl.Tests;

[TestClass]
public sealed class Given_AdvancedInput_ServiceAndCursor
{
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
	[Description("An implicit service factory exception remains an application failure rather than invalid user input.")]
	public async Task When_AnImplicitFactoryThrows_Then_InputValidationDoesNotReclassifyIt()
	{
		var app = ReplApp.Create(services => services.AddTransient<string>(_ => throw new InvalidOperationException("factory failed")));
		var validated = false;
		app.Map("query", (string prefix, int limit) => limit);
		app.UseInputValidator((ParsedInputContext _) => { validated = true; return InputValidationResult.Valid(); });
		(await app.RunAsync(["query", "3"])).Should().Be(1);
		validated.Should().BeFalse();
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
