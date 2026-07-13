using Microsoft.Extensions.DependencyInjection;

namespace Repl.IntegrationTests;

/// <summary>
/// End-to-end regressions for shell-scoped WithCompletion providers through the public
/// <c>completion __complete</c> bridge (PR #58 review follow-ups).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Given_ShellCompletionBridge_Providers
{
	[TestMethod]
	[Description("The bridge invokes providers with the RUN service provider, not the core-only fallback: a shell-scoped provider resolving an externally-registered service through CompletionContext.Services must see it — the same provider already works on the interactive path.")]
	public void When_ProviderResolvesExternalService_Then_BridgeUsesRunServiceProvider()
	{
		var sut = ReplApp.Create(static services =>
			services.AddSingleton<IClientDirectory>(new ClientDirectory()));
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (context, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(
					[
						context.Services.GetService(typeof(IClientDirectory)) is IClientDirectory directory
							? directory.Marker
							: "missing-di",
					]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");

		var output = RunBridge(sut, "app deploy ");

		output.Text.Should().Contain("external-di", because: "the provider must resolve services registered on the run's DI container");
		output.Text.Should().NotContain("missing-di");
		output.ExitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("The bridge protocol is line-delimited: a provider value with an embedded newline would forge an extra completion record, and ANSI/OSC control sequences would reach the user's completion UI. Such candidates are rejected whole at the bridge boundary; clean values still flow.")]
	public void When_ProviderReturnsControlCharacters_Then_BridgeRejectsThoseCandidates()
	{
		var sut = ReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(
					["safe\nforged", "\u001b[31mansi-red", "\u009dosc-c1", "clean"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");

		var output = RunBridge(sut, "app deploy ");

		output.Text.Should().Contain("clean", because: "well-formed values still flow through the protocol");
		output.Text.Should().NotContain("forged", because: "an embedded LF must not forge an extra completion record");
		output.Text.Should().NotContain("\u001b", because: "terminal control sequences must not reach the shell's completion UI");
		output.Text.Should().NotContain("\u009d", because: "C1 controls (OSC introducer) are as dangerous as ESC sequences");
		output.ExitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("The same rejection applies to a pending option's provider values: 'app run --channel ' with a provider returning a newline-embedded value must not leak the forged record through the bridge.")]
	public void When_PendingOptionProviderReturnsControlCharacters_Then_BridgeRejectsThoseCandidates()
	{
		var sut = ReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none")
			.WithCompletion(
				"channel",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["alpha\nforged", "beta"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Run.");

		var output = RunBridge(sut, "app run --channel ");

		output.Text.Should().Contain("beta");
		output.Text.Should().NotContain("forged");
		output.ExitCode.Should().Be(0);
	}

	private static (int ExitCode, string Text) RunBridge(ReplApp app, string line) =>
		ConsoleCaptureHelper.Capture(() => app.Run(
		[
			"completion",
			"__complete",
			"--shell",
			"bash",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
			"--no-interactive",
			"--no-logo",
		]));

	private interface IClientDirectory
	{
		string Marker { get; }
	}

	private sealed class ClientDirectory : IClientDirectory
	{
		public string Marker => "external-di";
	}
}
