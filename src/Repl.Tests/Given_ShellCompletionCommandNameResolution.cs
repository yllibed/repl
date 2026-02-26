namespace Repl.Tests;

[TestClass]
public sealed class Given_ShellCompletionCommandNameResolution
{
	[TestMethod]
	[Description("Regression guard: verifies command-line head takes precedence over current process host name for shell completion registration.")]
	public void When_CommandLineHeadIsPresent_Then_CommandHeadWins()
	{
		var args = new[] { "/apps/Repl.ShellCompletionTestHost" };

		var result = CoreReplApp.ResolveShellCompletionCommandName(
			args,
			processPath: "/usr/share/dotnet/dotnet",
			fallbackName: "fallback");

		result.Should().Be("Repl.ShellCompletionTestHost");
	}

	[TestMethod]
	[Description("Regression guard: verifies process path is used when command-line head is unavailable.")]
	public void When_CommandLineHeadMissing_Then_ProcessHeadIsUsed()
	{
		var result = CoreReplApp.ResolveShellCompletionCommandName(
			commandLineArgs: [],
			processPath: "/usr/share/dotnet/dotnet",
			fallbackName: "fallback");

		result.Should().Be("dotnet");
	}

	[TestMethod]
	[Description("Regression guard: verifies managed launcher arguments keep dotted command names and trim only known executable suffixes.")]
	public void When_CommandLineHeadIsDll_Then_DllSuffixIsTrimmedOnly()
	{
		var args = new[] { "/apps/Repl.ShellCompletionTestHost.dll" };

		var result = CoreReplApp.ResolveShellCompletionCommandName(
			args,
			processPath: "/usr/share/dotnet/dotnet",
			fallbackName: "fallback");

		result.Should().Be("Repl.ShellCompletionTestHost");
	}

	[TestMethod]
	[Description("Regression guard: verifies fallback name is used when command-line and process heads are unavailable.")]
	public void When_CommandAndProcessHeadsMissing_Then_FallbackIsUsed()
	{
		var result = CoreReplApp.ResolveShellCompletionCommandName(
			commandLineArgs: [],
			processPath: null,
			fallbackName: "my-repl");

		result.Should().Be("my-repl");
	}

	[TestMethod]
	[Description("Regression guard: verifies hardcoded fallback remains repl when no source name can be resolved.")]
	public void When_AllSourcesMissing_Then_DefaultIsRepl()
	{
		var result = CoreReplApp.ResolveShellCompletionCommandName(
			commandLineArgs: [],
			processPath: null,
			fallbackName: null);

		result.Should().Be("repl");
	}
}
