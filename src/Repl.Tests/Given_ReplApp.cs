using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

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
	[Description("Regression guard: verifies routing cache can be invalidated explicitly through public API.")]
	public void When_InvalidatingRouting_Then_PublicApiIsAvailable()
	{
		var sut = ReplApp.Create();

		var action = () => sut.InvalidateRouting();

		action.Should().NotThrow();
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



	[TestMethod]
	[Description("A retained ParsingOptions instance must still validate mapped token collisions after the Options callback has returned.")]
	public void When_RetainedParsingOptionsCreatesMappedCollision_Then_ChangeIsRejectedAndRolledBack()
	{
		var sut = CoreReplApp.Create();
		sut.Map(
			"deploy",
			static string (
				[ReplOption(Aliases = ["--MODE"])] string? primary = null,
				[ReplOption(Aliases = ["--mode"])] string? secondary = null) => $"{primary}:{secondary}");
		var retained = sut.OptionsSnapshot.Parsing;

		var action = () => retained.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive;

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*Option token collision*--mode*deploy*");
		retained.OptionCaseSensitivity.Should().Be(ReplCaseSensitivity.CaseSensitive);
	}

	[TestMethod]
	[Description("An invalid comparer transition is rejected at the mutator before later callback statements can observe or build on ambiguous routing.")]
	public void When_OptionsCallbackCreatesCollision_Then_TransitionIsRejectedBeforeCallbackContinues()
	{
		var sut = CoreReplApp.Create();
		sut.Map(
			"deploy",
			static string (
				[ReplOption(Aliases = ["--MODE"])] string? primary = null,
				[ReplOption(Aliases = ["--mode"])] string? secondary = null) => $"{primary}:{secondary}");

		var callbackContinued = false;
		var action = () => sut.Options(options =>
		{
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive;
			callbackContinued = true;
		});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*Option token collision*--mode*deploy*");
		callbackContinued.Should().BeFalse();
		sut.OptionsSnapshot.Parsing.OptionCaseSensitivity.Should().Be(ReplCaseSensitivity.CaseSensitive);
	}

	[TestMethod]
	[Description("Routing subscriber failures are deferred until a successful Options callback finishes, so subscriber code cannot interrupt the configuration body.")]
	public void When_OptionsMutationSubscriberFails_Then_CallbackCompletesBeforeFailureIsRethrown()
	{
		var sut = CoreReplApp.Create();
		var callbackCompleted = false;
		var detailedCount = 0;
		sut.RoutingInvalidated += (_, _) => throw new InvalidOperationException("Legacy subscriber failed.");
		sut.RoutingInvalidatedDetailed += (_, _) => detailedCount++;

		var action = () => sut.Options(options =>
		{
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive;
			callbackCompleted = true;
		});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("Legacy subscriber failed.");
		callbackCompleted.Should().BeTrue();
		detailedCount.Should().Be(1);
	}

	[TestMethod]
	[Description("A configuration exception remains primary when a routing subscriber also fails, while detailed retraction subscribers are still notified.")]
	public void When_OptionsCallbackAndRoutingSubscriberFail_Then_CallbackExceptionIsPreserved()
	{
		var sut = CoreReplApp.Create();
		var detailedCount = 0;
		sut.RoutingInvalidated += (_, _) => throw new InvalidOperationException("Legacy subscriber failed.");
		sut.RoutingInvalidatedDetailed += (_, _) => detailedCount++;

		var action = () => sut.Options(options =>
		{
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive;
			throw new FormatException("Configuration failed after mutation.");
		});

		action.Should().Throw<FormatException>()
			.WithMessage("Configuration failed after mutation.");
		detailedCount.Should().Be(1);
	}

	[TestMethod]
	[Description("The detailed invalidation subscriber is always notified even when the legacy binary-compatible subscriber throws.")]
	public void When_LegacyRoutingSubscriberThrows_Then_DetailedSubscriberStillRuns()
	{
		var sut = CoreReplApp.Create();
		var detailedCount = 0;
		sut.RoutingInvalidated += (_, _) => throw new InvalidOperationException("Legacy subscriber failed.");
		sut.RoutingInvalidatedDetailed += (_, _) => detailedCount++;

		var action = sut.InvalidateRouting;

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("Legacy subscriber failed.");
		detailedCount.Should().Be(1);
	}

	[TestMethod]
	[Description("A task inheriting an Options exception scope after that scope closes surfaces subscriber failures on its own execution path instead of losing them.")]
	public void When_InheritedOptionsScopeIsClosed_Then_LateSubscriberFailureIsNotSwallowed()
	{
		var sut = CoreReplApp.Create();
		sut.RoutingInvalidated += (_, _) => throw new InvalidOperationException("Subscriber failed late.");
		using var releaseMutation = new ManualResetEventSlim(initialState: false);
		using var mutationCompleted = new ManualResetEventSlim(initialState: false);
		Exception? observedFailure = null;

		sut.Options(options =>
		{
			_ = Task.Run(() =>
			{
				releaseMutation.Wait();
				try
				{
					options.Parsing.AddGlobalOption<string>("late");
				}
				catch (Exception exception)
				{
					observedFailure = exception;
				}
				finally
				{
					mutationCompleted.Set();
				}
			});
		});
		releaseMutation.Set();

		mutationCompleted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
		observedFailure.Should().BeOfType<InvalidOperationException>()
			.Which.Message.Should().Be("Subscriber failed late.");
	}

	[TestMethod]
	[Description("Changing the global comparer after mapping is rejected and rolled back when two previously distinct route-option tokens would become indistinguishable.")]
	public void When_CaseSensitivityChangeCreatesMappedOptionCollision_Then_ChangeIsRejectedAndRolledBack()
	{
		var sut = CoreReplApp.Create();
		sut.Map(
			"deploy",
			static string (
				[ReplOption(Aliases = ["--MODE"])] string? primary = null,
				[ReplOption(Aliases = ["--mode"])] string? secondary = null) => $"{primary}:{secondary}");

		var action = () => sut.Options(options =>
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*Option token collision*--mode*deploy*");
		sut.OptionsSnapshot.Parsing.OptionCaseSensitivity.Should().Be(ReplCaseSensitivity.CaseSensitive);
	}

	[TestMethod]
	[Description("Comparer validation and route publication share one configuration gate, so a route built concurrently cannot slip between validation and comparer publication.")]
	public async Task When_RouteMappingRacesValidatedCaseTransition_Then_RouteUsesPublishedComparer()
	{
		var sut = CoreReplApp.Create();
		var parsing = sut.OptionsSnapshot.Parsing;
		var validatorField = typeof(ParsingOptions).GetField(
			"_validateOptionCaseSensitivityChange",
			BindingFlags.Instance | BindingFlags.NonPublic)!;
		var originalValidator = (Action<ReplCaseSensitivity>)validatorField.GetValue(parsing)!;
		using var validationCompleted = new ManualResetEventSlim(initialState: false);
		using var releasePublication = new ManualResetEventSlim(initialState: false);
		using var mappingStarted = new ManualResetEventSlim(initialState: false);
		validatorField.SetValue(parsing, (Action<ReplCaseSensitivity>)(caseSensitivity =>
		{
			originalValidator(caseSensitivity);
			validationCompleted.Set();
			releasePublication.Wait();
		}));

		var transition = Task.Run(() => parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);
		validationCompleted.Wait();
		var mapping = Task.Run<Exception?>(() =>
		{
			mappingStarted.Set();
			try
			{
				sut.Map(
					"deploy",
					static string (
						[ReplOption(Aliases = ["--MODE"])] string? primary = null,
						[ReplOption(Aliases = ["--mode"])] string? secondary = null) => $"{primary}:{secondary}");
				return null;
			}
			catch (Exception exception)
			{
				return exception;
			}
		});
		mappingStarted.Wait();
		var mappingCompletedBeforePublication = SpinWait.SpinUntil(() => mapping.IsCompleted, TimeSpan.FromSeconds(1));
		releasePublication.Set();

		await transition.ConfigureAwait(false);
		var mappingFailure = await mapping.ConfigureAwait(false);

		mappingCompletedBeforePublication.Should().BeFalse();
		mappingFailure.Should().BeOfType<InvalidOperationException>()
			.Which.Message.Should().Contain("Option token collision").And.Contain("--mode");
		parsing.OptionCaseSensitivity.Should().Be(ReplCaseSensitivity.CaseInsensitive);
	}

	[TestMethod]
	[Description("A configuration callback that mutates discovery-affecting options and then throws still invalidates routing, because the mutable options object keeps the applied change.")]
	public void When_OptionsCallbackMutatesCaseSensitivityAndThrows_Then_RoutingIsInvalidated()
	{
		var sut = CoreReplApp.Create();
		var invalidationCount = 0;
		sut.RoutingInvalidated += (_, _) => invalidationCount++;

		var action = () => sut.Options(options =>
		{
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive;
			throw new InvalidOperationException("Configuration failed after mutation.");
		});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("Configuration failed after mutation.");
		invalidationCount.Should().Be(1);
		sut.OptionsSnapshot.Parsing.OptionCaseSensitivity.Should().Be(ReplCaseSensitivity.CaseInsensitive);
	}

	[TestMethod]
	[Description("A programmatic caller compiled before discovery-aware argument validation must fail closed before its reconstructed tokens can execute.")]
	public async Task When_ProgrammaticInvocationHasNoCurrentContract_Then_InvocationFailsClosed()
	{
		var invoked = false;
		var sut = CoreReplApp.Create();
		sut.Map("deploy", () => invoked = true);
		using var output = new StringWriter();
		using var session = ReplSessionIO.SetSession(
			output,
			TextReader.Null,
			commandOutput: output,
			error: output);
		var previousProgrammatic = ReplSessionIO.IsProgrammatic;
		ReplSessionIO.IsProgrammatic = true;

		int exitCode;
		try
		{
			exitCode = await sut.RunSubInvocationAsync(
				["--no-logo", "deploy"],
				EmptyServiceProvider.Instance);
		}
		finally
		{
			ReplSessionIO.IsProgrammatic = previousProgrammatic;
		}

		exitCode.Should().Be(1);
		invoked.Should().BeFalse();
		output.ToString().Should().Contain("incompatible", Exactly.Once());
	}

	[TestMethod]
	[Description("A programmatic adapter must attest its own compiled contract version; a stale version fails closed before handler execution.")]
	public async Task When_ProgrammaticInvocationAttestsStaleContract_Then_InvocationFailsClosed()
	{
		var invoked = false;
		var sut = CoreReplApp.Create();
		sut.Map("deploy", () => invoked = true);
		using var output = new StringWriter();
		using var session = ReplSessionIO.SetSession(
			output,
			TextReader.Null,
			commandOutput: output,
			error: output);
		var previousProgrammatic = ReplSessionIO.IsProgrammatic;
		ReplSessionIO.IsProgrammatic = true;

		int exitCode;
		try
		{
			using var contract = ReplSessionIO.PushProgrammaticInvocationContract(adapterContractVersion: 0);
			exitCode = await sut.RunSubInvocationAsync(
				["--no-logo", "deploy"],
				EmptyServiceProvider.Instance);
		}
		finally
		{
			ReplSessionIO.IsProgrammatic = previousProgrammatic;
		}

		exitCode.Should().Be(1);
		invoked.Should().BeFalse();
		output.ToString().Should().Contain("incompatible", Exactly.Once());
	}

	[TestMethod]
	[Description("The legacy EventHandler routing-invalidation accessor remains present so an older separately packaged Repl.Mcp assembly can subscribe when paired with a newer Repl.Core.")]
	public void When_InspectingRoutingInvalidatedEvent_Then_LegacyAccessorSignatureIsPreserved()
	{
		var addAccessor = typeof(CoreReplApp).GetMethod(
			"add_RoutingInvalidated",
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			types: [typeof(EventHandler)],
			modifiers: null);

		addAccessor.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Regression guard: ReplApp.Services must publish exactly one root provider even when several callers race to build it — a caller-owned host that opens more than one session against one shared app (a supported shape: Repl.Testing's own CreateProbeHost pattern) resolves Services from more than one thread. The lazy '??=' this used to read from is a check-then-assign, not an atomic publish, so a race could build two providers and silently orphan whichever one lost — along with any disposable singleton the loser had already started constructing.")]
	public async Task When_ServicesIsRacedFromManyThreads_Then_EveryCallerObservesTheSameProvider()
	{
		var sut = ReplApp.Create(services => services.AddSingleton<object>());

		var providers = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => sut.Services)));

		providers.Should().OnlyContain(provider => ReferenceEquals(provider, providers[0]));
	}

	private sealed class StubTransformer : IOutputTransformer
	{
		public string Name => "stub";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(value?.ToString() ?? string.Empty);
	}

	private sealed class EmptyServiceProvider : IServiceProvider
	{
		public static readonly EmptyServiceProvider Instance = new();
		public object? GetService(Type serviceType) => null;
	}

	private sealed class EmptyModule : IReplModule
	{
		public void Map(IReplMap map)
		{
		}
	}
}
