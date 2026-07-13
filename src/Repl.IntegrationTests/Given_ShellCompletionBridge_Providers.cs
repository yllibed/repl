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
