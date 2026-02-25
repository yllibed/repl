using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ReplApp
{
	[TestMethod]
	[Description("Regression guard: verifies creating default instance so that app is returned.")]
	public void When_CreatingDefaultInstance_Then_AppIsReturned()
	{
		var sut = ReplApp.Create();

		sut.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies configuring options so that configuration is applied.")]
	public void When_ConfiguringOptions_Then_ConfigurationIsApplied()
	{
		var sut = ReplApp.Create();
		var observedPrompt = string.Empty;

		sut.Options(options =>
		{
			options.Interactive.Prompt = "myrepl>";
			observedPrompt = options.Interactive.Prompt;
		});

		observedPrompt.Should().Be("myrepl>");
	}

	[TestMethod]
	[Description("Regression guard: verifies shell completion setup defaults so apps start in explicit manual install mode.")]
	public void When_InspectingShellCompletionDefaults_Then_FeatureIsEnabledAndModeIsManual()
	{
		var sut = ReplApp.Create();
		var enabled = false;
		var mode = ShellCompletionSetupMode.Auto;

		sut.Options(options =>
		{
			enabled = options.ShellCompletion.Enabled;
			mode = options.ShellCompletion.SetupMode;
		});

		enabled.Should().BeTrue();
		mode.Should().Be(ShellCompletionSetupMode.Manual);
	}

	[TestMethod]
	[Description("Regression guard: verifies registering duplicate transformer so that exception is thrown.")]
	public void When_RegisteringDuplicateTransformer_Then_ExceptionIsThrown()
	{
		var sut = ReplApp.Create();

		sut.Options(options => options.Output.AddTransformer("csv", new StubTransformer()));
		var action = () => sut.Options(options => options.Output.AddTransformer("csv", new StubTransformer()));

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*already registered*");
	}

	[TestMethod]
	[Description("Regression guard: verifies creating text result so that result kind is text.")]
	public void When_CreatingTextResult_Then_ResultKindIsText()
	{
		var result = Results.Text("ok");

		result.Kind.Should().Be("text");
		result.Message.Should().Be("ok");
		result.Code.Should().BeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies registering duplicate custom route constraint so that exception is thrown.")]
	public void When_RegisteringDuplicateCustomRouteConstraint_Then_ExceptionIsThrown()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.Parsing.AddRouteConstraint("slug", static value => value.Length > 0));

		var action = () => sut.Options(options => options.Parsing.AddRouteConstraint("slug", static value => value.Length > 0));

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*already registered*");
	}

	[TestMethod]
	[Description("Regression guard: verifies registering reserved built-in route constraint name so that exception is thrown.")]
	public void When_RegisteringReservedConstraintName_Then_ExceptionIsThrown()
	{
		var sut = ReplApp.Create();

		var action = () => sut.Options(options => options.Parsing.AddRouteConstraint("url", static value => value.Length > 0));

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*reserved*");
	}

	[TestMethod]
	[Description("Regression guard: verifies registering reserved alias route constraint name so that exception is thrown.")]
	public void When_RegisteringReservedAliasConstraintName_Then_ExceptionIsThrown()
	{
		var sut = ReplApp.Create();

		var action = () => sut.Options(options => options.Parsing.AddRouteConstraint("time-span", static value => value.Length > 0));

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*reserved*");
	}

	[TestMethod]
	[Description("Regression guard: verifies creating app from factory so that composition entrypoint remains available.")]
	public void When_CreatingFromFactory_Then_AppIsReturned()
	{
		var sut = ReplAppFactory.Create();

		sut.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies injectable module presence predicate must return bool.")]
	public void When_MappingModuleWithInjectablePresencePredicateReturningNonBool_Then_ExceptionIsThrown()
	{
		var sut = ReplApp.Create();
		Delegate predicate = (Func<int>)(() => 1);

		var action = () => sut.MapModule(new EmptyModule(), predicate);

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*must return bool*");
	}

	[TestMethod]
	[Description("Regression guard: verifies injectable module presence predicate cannot request IServiceProvider directly.")]
	public void When_MappingModuleWithInjectablePresencePredicateUsingServiceProvider_Then_ExceptionIsThrown()
	{
		var sut = ReplApp.Create();
		Delegate predicate = (Func<IServiceProvider, bool>)(_ => true);

		var action = () => sut.MapModule(new EmptyModule(), predicate);

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*cannot declare IServiceProvider*");
	}

	private sealed class StubTransformer : IOutputTransformer
	{
		public string Name => "stub";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(value?.ToString() ?? string.Empty);
	}

	private sealed class EmptyModule : IReplModule
	{
		public void Map(IReplMap map)
		{
		}
	}
}



