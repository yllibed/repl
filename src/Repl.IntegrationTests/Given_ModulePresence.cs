using Microsoft.Extensions.DependencyInjection;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ModulePresence
{
	[TestMethod]
	[Description("Regression guard: verifies module presence can switch within an interactive session when state changes and routing cache is explicitly invalidated.")]
	public void When_ModulePresenceDependsOnSessionState_Then_CommandGraphSwitchesAfterInvalidation()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.MapModule(new SignedOutExperienceModule(), static context => !IsSignedIn(context.SessionState));
		sut.MapModule(new SignedInExperienceModule(), static context => IsSignedIn(context.SessionState));

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"profile whoami\nauth login\nprofile whoami\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("guest");
		output.Text.Should().Contain("member");
	}

	[TestMethod]
	[Description("Regression guard: verifies when multiple active modules map the same route, the latest registration wins.")]
	public void When_MultipleActiveModulesShareSameRoute_Then_LastRegisteredModuleWins()
	{
		var sut = ReplApp.Create();
		sut.MapModule(new StaticIdentityModule("first"));
		sut.MapModule(new StaticIdentityModule("second"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["identity", "whoami", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("second");
	}

	[TestMethod]
	[Description("Regression guard: verifies module presence can target CLI channel only so the same commands are absent in hosted session channel.")]
	public void When_ModuleIsCliOnly_Then_HostedSessionDoesNotResolveIt()
	{
		var sut = ReplApp.Create();
		sut.MapModule(
			new CliOnlyOpsModule(),
			static context => context.Channel == ReplRuntimeChannel.Cli);

		var cli = ConsoleCaptureHelper.Capture(() => sut.Run(["ops", "ping", "--no-logo"]));
		cli.ExitCode.Should().Be(0);
		cli.Text.Should().Contain("pong");

		using var input = new StringReader(string.Empty);
		using var output = new StringWriter();
		var hostedExitCode = sut.Run(["ops", "ping", "--no-logo"], new InMemoryHost(input, output));

		hostedExitCode.Should().Be(1);
		output.ToString().Should().Contain("Unknown command");
	}

	[TestMethod]
	[Description("Regression guard: verifies injectable module presence predicates can resolve services through DI in ReplApp and reflect updates after explicit routing invalidation.")]
	public void When_ModulePresenceUsesInjectableServicePredicate_Then_ModuleAvailabilityFollowsServiceState()
	{
		var gate = new PresenceGate { Enabled = true };
		var sut = ReplApp.Create(services => services.AddSingleton(gate));
		sut.MapModule(new GatedFeatureModule(), (PresenceGate value) => value.Enabled);

		var enabled = ConsoleCaptureHelper.Capture(() => sut.Run(["feature", "ping", "--no-logo"]));
		enabled.ExitCode.Should().Be(0);
		enabled.Text.Should().Contain("pong");

		gate.Enabled = false;
		sut.InvalidateRouting();
		var disabled = ConsoleCaptureHelper.Capture(() => sut.Run(["feature", "ping", "--no-logo"]));
		disabled.ExitCode.Should().Be(1);
		disabled.Text.Should().Contain("Unknown command");
	}

	[TestMethod]
	[Description("Regression guard: verifies injectable module presence predicates can read runtime channel without IServiceProvider parameter.")]
	public void When_ModulePresenceUsesInjectableChannelPredicate_Then_HostedSessionDoesNotResolveIt()
	{
		var sut = ReplApp.Create();
		sut.MapModule(
			new CliOnlyOpsModule(),
			(ReplRuntimeChannel channel) => channel == ReplRuntimeChannel.Cli);

		var cli = ConsoleCaptureHelper.Capture(() => sut.Run(["ops", "ping", "--no-logo"]));
		cli.ExitCode.Should().Be(0);
		cli.Text.Should().Contain("pong");

		using var input = new StringReader(string.Empty);
		using var output = new StringWriter();
		var hostedExitCode = sut.Run(["ops", "ping", "--no-logo"], new InMemoryHost(input, output));

		hostedExitCode.Should().Be(1);
		output.ToString().Should().Contain("Unknown command");
	}

	[TestMethod]
	[Description("Regression guard: verifies scoped ReplApp mappings can use injectable module presence predicates and reflect updates after explicit routing invalidation.")]
	public void When_ScopedMapModuleUsesInjectablePredicate_Then_ModuleAvailabilityFollowsServiceState()
	{
		var gate = new PresenceGate { Enabled = true };
		var sut = ReplApp.Create(services => services.AddSingleton(gate));
		IReplApp app = sut;
		app.Context("tenant", scoped =>
		{
			scoped.MapModule(new TenantFeatureModule(), (PresenceGate value) => value.Enabled);
		});

		var enabled = ConsoleCaptureHelper.Capture(() => sut.Run(["tenant", "feature", "ping", "--no-logo"]));
		enabled.ExitCode.Should().Be(0);
		enabled.Text.Should().Contain("pong");

		gate.Enabled = false;
		sut.InvalidateRouting();
		var disabled = ConsoleCaptureHelper.Capture(() => sut.Run(["tenant", "feature", "ping", "--no-logo"]));
		disabled.ExitCode.Should().Be(1);
		disabled.Text.Should().Contain("Unknown command");
	}

	[TestMethod]
	[Description("Regression guard: verifies module presence graph remains stable within one interactive session until explicit invalidation is requested.")]
	public void When_ModulePresenceServiceStateChangesWithoutInvalidation_Then_PreviousRoutingGraphRemainsActive()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.MapModule(new SignedOutWithoutInvalidationModule(), static context => !IsSignedIn(context.SessionState));
		sut.MapModule(new SignedInExperienceModule(), static context => IsSignedIn(context.SessionState));

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"profile whoami\nauth login\nprofile whoami\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		CountOccurrences(output.Text, "guest").Should().BeGreaterThanOrEqualTo(2);
		output.Text.Should().NotContain("member");
	}

	private static bool IsSignedIn(IReplSessionState sessionState)
	{
		return sessionState.TryGet<bool>("auth.signed_in", out var signedIn) && signedIn;
	}

	private sealed class SignedOutExperienceModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("auth", auth =>
			{
				auth.Map("login", (IReplSessionState state, ICoreReplApp app) =>
				{
					state.Set(key: "auth.signed_in", value: true);
					app.InvalidateRouting();
					return "ok";
				});
			});

			map.Context("profile", profile =>
			{
				profile.Map("whoami", () => "guest");
			});
		}
	}

	private sealed class SignedOutWithoutInvalidationModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("auth", auth =>
			{
				auth.Map("login", (IReplSessionState state) =>
				{
					state.Set(key: "auth.signed_in", value: true);
					return "ok";
				});
			});

			map.Context("profile", profile =>
			{
				profile.Map("whoami", () => "guest");
			});
		}
	}

	private sealed class SignedInExperienceModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("profile", profile =>
			{
				profile.Map("whoami", () => "member");
			});
		}
	}

	private sealed class StaticIdentityModule(string value) : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("identity", identity =>
			{
				identity.Map("whoami", () => value);
			});
		}
	}

	private sealed class CliOnlyOpsModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("ops", ops =>
			{
				ops.Map("ping", () => "pong");
			});
		}
	}

	private sealed class GatedFeatureModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("feature", feature =>
			{
				feature.Map("ping", () => "pong");
			});
		}
	}

	private sealed class TenantFeatureModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("feature", feature =>
			{
				feature.Map("ping", () => "pong");
			});
		}
	}

	private sealed class PresenceGate
	{
		public bool Enabled { get; set; }
	}

	private static int CountOccurrences(string source, string value)
	{
		if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value))
		{
			return 0;
		}

		return source.Split([value], StringSplitOptions.None).Length - 1;
	}

	private sealed class InMemoryHost(TextReader input, TextWriter output) : IReplHost
	{
		public TextReader Input { get; } = input;

		public TextWriter Output { get; } = output;
	}
}
