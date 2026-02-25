using System.Globalization;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ShellCompletion
{
	[TestMethod]
	[Description("Regression guard: verifies bash shell completion on root prefix so that literal command candidates are returned.")]
	public void When_CompletingRootPrefixInBash_Then_LiteralCommandsAreReturned()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok");
		sut.Map("config set", () => "ok");

		const string line = "repl c";
		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
		[
			"completion",
			"__complete",
			"--shell",
			"bash",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("config");
		output.Text.Should().Contain("contact");
	}

	[TestMethod]
	[Description("Regression guard: verifies PowerShell completion for nested command path so that next literal segments are returned.")]
	public void When_CompletingNestedPathInPowerShell_Then_NextLiteralsAreReturned()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok");
		sut.Map("contact remove", () => "ok");
		sut.Map("contact show {id:int}", (Func<int, string>)(id => id.ToString(CultureInfo.InvariantCulture)));

		const string line = "repl contact ";
		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
		[
			"completion",
			"__complete",
			"--shell",
			"powershell",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("list");
		output.Text.Should().Contain("remove");
		output.Text.Should().Contain("show");
	}

	[TestMethod]
	[Description("Regression guard: verifies option completion on resolved command route so that static handler options are suggested.")]
	public void When_CompletingCommandOptionPrefix_Then_StaticRouteOptionsAreReturned()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"contact show {id:int}",
			(Func<int, bool, string, string>)((id, verbose, label) =>
				$"{id}-{verbose}-{label}"));

		const string line = "repl contact show 42 --v";
		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
		[
			"completion",
			"__complete",
			"--shell",
			"bash",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("--verbose");
		output.Text.Should().NotContain("--id");
	}

	[TestMethod]
	[Description("Regression guard: verifies global option completion so that known static global options are suggested.")]
	public void When_CompletingGlobalOptionPrefix_Then_GlobalOptionsAreReturned()
	{
		var sut = ReplApp.Create();
		sut.Map("ping", () => "pong");

		const string line = "repl --no";
		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
		[
			"completion",
			"__complete",
			"--shell",
			"powershell",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("--no-interactive");
		output.Text.Should().Contain("--no-logo");
	}

	[TestMethod]
	[Description("Regression guard: verifies hidden commands are excluded from shell completion suggestions.")]
	public void When_CommandIsHidden_Then_ShellCompletionDoesNotExposeIt()
	{
		var sut = ReplApp.Create();
		sut.Map("send", () => "ok");
		sut.Map("secret ping", () => "ok").Hidden();

		const string line = "repl se";
		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
		[
			"completion",
			"__complete",
			"--shell",
			"bash",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain("secret");
		output.Text.Should().Contain("send");
	}

	[TestMethod]
	[Description("Regression guard: verifies dynamic context values are not auto-completed in shell completion V1.")]
	public void When_OnlyDynamicContextValueIsExpected_Then_NoCandidatesAreReturned()
	{
		var sut = ReplApp.Create();
		sut.Context("client", client =>
		{
			client.Context("{id}", scoped =>
			{
				scoped.Map("show", (Func<string, string>)(id => id));
			});
		});

		const string line = "repl client ";
		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
		[
			"completion",
			"__complete",
			"--shell",
			"powershell",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]));

		output.ExitCode.Should().Be(0);
		output.Text.Trim().Should().BeEmpty();
	}

	[TestMethod]
	[Description("Regression guard: verifies invalid shell completion usage fails with exit code 2 and usage guidance on stderr.")]
	public void When_ShellCompletionUsageIsInvalid_Then_ExitCodeTwoAndUsageAreReturned()
	{
		var sut = ReplApp.Create();
		sut.Map("ping", () => "pong");
		var previousError = Console.Error;
		using var errorWriter = new StringWriter();
		Console.SetError(errorWriter);

		try
		{
			var output = ConsoleCaptureHelper.Capture(() => sut.Run(
			[
				"completion",
				"__complete",
				"--shell",
				"bash",
				"--line",
				"repl ping",
				"--cursor",
				"invalid",
			]));

			output.ExitCode.Should().Be(2);
			errorWriter.ToString().Should().Contain("usage: completion __complete");
		}
		finally
		{
			Console.SetError(previousError);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies existing complete ambient command behavior remains unchanged after adding shell completion command.")]
	public void When_UsingExistingCompleteCommand_Then_CompletionProviderBehaviorIsUnchanged()
	{
		var sut = ReplApp.Create();
		sut.Map("contact inspect", () => "ok")
			.WithCompletion("clientId", static (_, input, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>([$"{input}A", $"{input}B"]));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
		[
			"complete",
			"contact",
			"inspect",
			"--target",
			"clientId",
			"--input",
			"x",
		]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("xA");
		output.Text.Should().Contain("xB");
	}
}
