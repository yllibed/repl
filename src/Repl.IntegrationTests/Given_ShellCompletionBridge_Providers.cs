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
	[Description("The PR's core safety premise, guarded end-to-end: a DEFAULT-scope (2-arg WithCompletion) provider is NEVER invoked by the shell bridge — its values must not appear in 'completion __complete' output, so an interactive-only/slow provider can't block a shell Tab.")]
	public void When_DefaultScopeProvider_Then_BridgeDoesNotInvokeIt()
	{
		var sut = ReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			// 2-arg overload → default CompletionProviderScope.Interactive (interactive only).
			.WithCompletion("target", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["interactive-only"]))
			.WithDescription("Deploy.");

		var output = RunBridge(sut, "app deploy ");

		output.Text.Should().NotContain("interactive-only",
			because: "a default-scope provider is interactive-only and must never run on the blocking shell bridge");
		output.ExitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("A shell-scoped provider value that is a GLOBAL option (stripped by GlobalOptionParser before routing, so it never binds to the segment) is not offered as a positional value through the bridge — parity with execution.")]
	public void When_ProviderValueIsGlobalOption_Then_BridgeDoesNotOfferItAsValue()
	{
		var sut = ReplApp.Create();
		sut.Options(static options => options.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["--tenant", "prod-server"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");

		var output = RunBridge(sut, "app deploy prod");

		output.Text.Should().Contain("prod-server", because: "an ordinary value still binds and completes");
		output.Text.Should().NotContain("--tenant",
			because: "the global parser strips --tenant before routing, so it never binds to {target}");
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

	[TestMethod]
	[Description("A provider that stalls (and ignores its cancellation token) is abandoned after ShellCompletion.ProviderTimeout: the bridge answers within the deadline with the remaining static candidates instead of blocking the invoking shell for the provider's full duration.")]
	public void When_ProviderStallsPastDeadline_Then_BridgeDegradesWithinTimeout()
	{
		var sut = ReplApp.Create();
		sut.Options(static options => options.ShellCompletion.ProviderTimeout = TimeSpan.FromMilliseconds(100));
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static async (_, _, _) =>
				{
					// Deliberately ignores the cancellation token — simulates a stalled network
					// call that the deadline must abandon rather than await.
					await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
					return (IReadOnlyList<string>)["slow-value"];
				},
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");

		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		var output = RunBridge(sut, "app deploy ");
		stopwatch.Stop();

		output.Text.Should().NotContain("slow-value", because: "the stalled provider is abandoned at the deadline");
		stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.5),
			because: "the bridge must answer at the deadline, not after the provider's full stall");
		output.ExitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("Provider values are encoded as LITERAL shell data, never interpolating syntax: a value of $(printf PWNED) must reach bash single-quoted — double quotes would let bash run the command substitution when the user accepts the candidate.")]
	public void When_ProviderValueContainsCommandSubstitution_Then_BridgeEmitsSingleQuotedLiteral()
	{
		var sut = ReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["$(printf PWNED)"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");

		var output = RunBridge(sut, "app deploy ");

		output.Text.Should().Contain("'$(printf PWNED)'",
			because: "single quotes are the only bash form where the value stays literal data");
		output.Text.Should().NotContain("\"$(",
			because: "a double-quoted candidate would execute the substitution in the user's shell");
		output.ExitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("The bridge deadline also covers SYNCHRONOUS provider work: a delegate that blocks with Thread.Sleep before returning its ValueTask must still be abandoned at ProviderTimeout instead of stalling the invoking shell for the full duration.")]
	public void When_ProviderBlocksSynchronously_Then_DeadlineStillApplies()
	{
		var sut = ReplApp.Create();
		sut.Options(static options => options.ShellCompletion.ProviderTimeout = TimeSpan.FromMilliseconds(100));
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) =>
				{
					// Synchronous blocking before the ValueTask exists — a plain WaitAsync on
					// the returned task cannot bound this.
					Thread.Sleep(TimeSpan.FromSeconds(3));
					return ValueTask.FromResult<IReadOnlyList<string>>(["slow-sync-value"]);
				},
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");

		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		var output = RunBridge(sut, "app deploy ");
		stopwatch.Stop();

		output.Text.Should().NotContain("slow-sync-value");
		stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.5),
			because: "synchronous provider work must not escape the bridge deadline");
		output.ExitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("A throwing provider must not disable shell completion: the fault is isolated per invocation, the bridge exits 0, and the static candidates (here the route options) are still emitted.")]
	public void When_ProviderThrowsSynchronously_Then_BridgeDegradesToStaticCandidates()
	{
		var sut = ReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel, [ReplOption] bool force) => channel ?? force.ToString())
			.WithCompletion(
				"channel",
				static (_, _, _) => throw new InvalidOperationException("probe"),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Run.");

		var output = RunBridge(sut, "app run --channel ");

		output.ExitCode.Should().Be(0, because: "a transient provider failure must not fail the completion invocation");
		output.Text.Should().NotContain("probe", because: "provider faults must not spam the shell's completion stream");
	}

	[TestMethod]
	[Description("The same isolation applies to an asynchronously faulting provider: the bridge still answers with exit 0 and the remaining static candidates instead of surfacing the exception.")]
	public void When_ProviderThrowsAsynchronously_Then_BridgeDegradesToStaticCandidates()
	{
		var sut = ReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static async (_, _, _) =>
				{
					await Task.Yield();
					throw new InvalidOperationException("probe");
				},
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");

		var output = RunBridge(sut, "app deploy ");

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain("probe");
	}

	[TestMethod]
	[Description("A provider that registers a BLOCKING cancellation callback must not hold the bridge past the deadline: cancellation runs off a detached task, so completion returns within the deadline even though the callback keeps running.")]
	public void When_ProviderCancellationCallbackBlocks_Then_BridgeStillReturnsWithinDeadline()
	{
		using var callbackEntered = new ManualResetEventSlim(initialState: false);
		using var releaseCallback = new ManualResetEventSlim(initialState: false);
		var sut = ReplApp.Create();
		sut.Options(static options => options.ShellCompletion.ProviderTimeout = TimeSpan.FromMilliseconds(100));
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				async (_, _, token) =>
				{
					// Registers a callback that blocks when the deadline cancels the token.
					using var registration = token.Register(() =>
					{
						callbackEntered.Set();
						releaseCallback.Wait(TimeSpan.FromSeconds(5));
					});
					await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
					return [];
				},
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");

		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		_ = RunBridge(sut, "app deploy ");
		stopwatch.Stop();

		releaseCallback.Set();
		stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
			because: "a blocking cancellation callback must not keep the bridge waiting past the deadline");
	}

	[TestMethod]
	[Description("On timeout the bridge cancels the provider's token so a COOPERATIVE provider stops promptly: a provider awaiting Task.Delay on its token must observe cancellation shortly after ProviderTimeout, not run forever after the bridge already returned.")]
	public void When_ProviderTimesOut_Then_ItsTokenIsCanceled()
	{
		using var canceled = new ManualResetEventSlim(initialState: false);
		var sut = ReplApp.Create();
		sut.Options(static options => options.ShellCompletion.ProviderTimeout = TimeSpan.FromMilliseconds(100));
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				async (_, _, token) =>
				{
					try
					{
						await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						canceled.Set();
						throw;
					}

					return [];
				},
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");

		_ = RunBridge(sut, "app deploy ");

		canceled.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue(
			because: "the deadline must cancel the provider token, not just abandon the task");
	}

	[TestMethod]
	[Description("A CoreReplApp-only app (no external DI container) still gets a non-null CompletionContext.Services through the bridge: the built-in provider resolves IServiceProvider to itself, so shell-scoped providers run instead of silently degrading to nothing.")]
	public void When_CoreReplAppRunsBridge_Then_ProviderGetsServiceProvider()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (context, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(
					[context.Services is null ? "ctx-null" : "ctx-ok"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy.");

		var output = ConsoleCaptureHelper.Capture(() => RunBridgeArgs(args => sut.Run(args), "app deploy "));

		output.Text.Should().Contain("ctx-ok", because: "the built-in provider must supply itself as IServiceProvider to the bridge");
	}

	private static (int ExitCode, string Text) RunBridge(ReplApp app, string line) =>
		ConsoleCaptureHelper.Capture(() => RunBridgeArgs(args => app.Run(args), line));

	private static int RunBridgeArgs(Func<string[], int> run, string line) =>
		run(
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
		]);

	private interface IClientDirectory
	{
		string Marker { get; }
	}

	private sealed class ClientDirectory : IClientDirectory
	{
		public string Marker => "external-di";
	}
}
