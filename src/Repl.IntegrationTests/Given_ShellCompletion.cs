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
		const string line = "repl c";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			"bash",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("config");
		output.Text.Should().Contain("contact");
	}

	[TestMethod]
	[Description("Regression guard: verifies zsh shell completion on root prefix so that literal command candidates are returned.")]
	public void When_CompletingRootPrefixInZsh_Then_LiteralCommandsAreReturned()
	{
		const string line = "repl c";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			"zsh",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("config");
		output.Text.Should().Contain("contact");
	}

	[TestMethod]
	[Description("Regression guard: verifies fish shell completion on root prefix so that literal command candidates are returned.")]
	public void When_CompletingRootPrefixInFish_Then_LiteralCommandsAreReturned()
	{
		const string line = "repl c";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			"fish",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("config");
		output.Text.Should().Contain("contact");
	}

	[TestMethod]
	[Description("Regression guard: verifies nushell completion on root prefix so that literal command candidates are returned.")]
	public void When_CompletingRootPrefixInNu_Then_LiteralCommandsAreReturned()
	{
		const string line = "repl c";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			"nu",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("config");
		output.Text.Should().Contain("contact");
	}

	[TestMethod]
	[Description("Regression guard: verifies PowerShell completion for nested command path so that next literal segments are returned.")]
	public void When_CompletingNestedPathInPowerShell_Then_NextLiteralsAreReturned()
	{
		const string line = "repl contact ";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			"powershell",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("list");
		output.Text.Should().Contain("remove");
		output.Text.Should().Contain("show");
	}

	[TestMethod]
	[DataRow("bash")]
	[DataRow("powershell")]
	[DataRow("zsh")]
	[DataRow("fish")]
	[DataRow("nu")]
	[Description("Regression guard: verifies option completion on resolved command routes across all supported shells so static typed parameters and flags remain discoverable.")]
	public void When_CompletingCommandOptionPrefix_Then_StaticRouteOptionsAreReturnedForAllShells(string shell)
	{
		const string line = "repl contact show 42 --v";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			shell,
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("--verbose");
		output.Text.Should().NotContain("--id");
	}

	[TestMethod]
	[DataRow("bash")]
	[DataRow("powershell")]
	[DataRow("zsh")]
	[DataRow("fish")]
	[DataRow("nu")]
	[Description("Regression guard: verifies named value-parameter option completion across all supported shells so typed option parameters remain discoverable.")]
	public void When_CompletingValueOptionPrefix_Then_NamedValueOptionIsReturnedForAllShells(string shell)
	{
		const string line = "repl contact show 42 --l";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			shell,
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("--label");
	}

	[TestMethod]
	[DataRow("bash")]
	[DataRow("powershell")]
	[DataRow("zsh")]
	[DataRow("fish")]
	[DataRow("nu")]
	[Description("Regression guard: verifies global option completion across all supported shells so built-in global flags stay consistently discoverable.")]
	public void When_CompletingGlobalOptionPrefix_Then_GlobalOptionsAreReturnedForAllShells(string shell)
	{
		const string line = "repl --no";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			shell,
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("--no-interactive");
		output.Text.Should().Contain("--no-logo");
	}

	[TestMethod]
	[DataRow("bash")]
	[DataRow("powershell")]
	[DataRow("zsh")]
	[DataRow("fish")]
	[DataRow("nu")]
	[Description("Regression guard: verifies enum option value completion across all supported shells so declared enum values remain discoverable everywhere.")]
	public void When_CompletingEnumOptionValue_Then_EnumValuesAreReturnedForAllShells(string shell)
	{
		const string line = "repl render --mode ";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			shell,
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Fast");
		output.Text.Should().Contain("Slow");
	}

	[TestMethod]
	[Description("Regression guard: verifies hidden commands are excluded from shell completion suggestions.")]
	public void When_CommandIsHidden_Then_ShellCompletionDoesNotExposeIt()
	{
		const string line = "repl se";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			"bash",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain("secret");
		output.Text.Should().Contain("send");
	}

	[TestMethod]
	[Description("Regression guard: verifies hidden contexts are excluded from shell completion root suggestions.")]
	public void When_ContextIsHidden_Then_ShellCompletionRootDoesNotExposeIt()
	{
		const string line = "repl a";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			"bash",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain("admin");
	}

	[TestMethod]
	[Description("Regression guard: verifies shell completion works inside a hidden context once it is addressed explicitly.")]
	public void When_CompletingWithinExplicitHiddenContext_Then_ChildCommandsAreReturned()
	{
		const string line = "repl admin ";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			"powershell",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("reset");
		output.Text.Should().Contain("status");
	}

	[TestMethod]
	[Description("Regression guard: verifies dynamic context values are not auto-completed in shell completion V1.")]
	public void When_OnlyDynamicContextValueIsExpected_Then_NoCandidatesAreReturned()
	{
		const string line = "repl client ";
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			"powershell",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Trim().Should().BeEmpty();
	}

	[TestMethod]
	[Description("Regression guard: verifies invalid shell completion usage fails through REPL error rendering with usage guidance.")]
	public void When_ShellCompletionUsageIsInvalid_Then_ErrorResultAndUsageAreReturned()
	{
		var output = Run(
		[
			"completion",
			"__complete",
			"--shell",
			"bash",
			"--line",
			"repl ping",
			"--cursor",
			"invalid",
		]);

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Error:");
		output.Text.Should().Contain("usage: completion __complete");
		output.Text.Should().Contain("bash|powershell|zsh|fish|nu");
	}

	[TestMethod]
	[Description("Regression guard: verifies existing complete ambient command behavior remains unchanged after adding shell completion command.")]
	public void When_UsingExistingCompleteCommand_Then_CompletionProviderBehaviorIsUnchanged()
	{
		var output = Run(
		[
			"complete",
			"contact",
			"inspect",
			"--target",
			"clientId",
			"--input",
			"x",
		]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("xA");
		output.Text.Should().Contain("xB");
	}

	private static (int ExitCode, string Text) Run(IReadOnlyList<string> args) =>
		ShellCompletionTestHostRunner.Run("completion", args);
}
