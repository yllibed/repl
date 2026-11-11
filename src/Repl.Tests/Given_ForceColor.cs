using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ForceColor
{
	[TestMethod]
	[Description("FORCE_COLOR colours a redirected stream, the way .NET 11's System.Console does.")]
	public void When_ForceColorIsSet_Then_ARedirectedStreamIsColoured()
	{
		using var env = WithEnv(("FORCE_COLOR", "1"));

		new OutputOptions().IsAnsiEnabled(isStreamRedirected: true).Should().BeTrue();
	}

	[TestMethod]
	[DataRow("0")]
	[DataRow("false")]
	[DataRow("1")]
	[Description("Any non-empty FORCE_COLOR forces colour, including 0, as System.Console does on .NET 11.")]
	public void When_ForceColorHasAnyNonEmptyValue_Then_ColourIsForced(string value)
	{
		using var env = WithEnv(("FORCE_COLOR", value));

		new OutputOptions().IsAnsiEnabled(isStreamRedirected: true).Should().BeTrue();
	}

	[TestMethod]
	[Description("An empty FORCE_COLOR is ignored, so redirection decides as usual.")]
	public void When_ForceColorIsEmpty_Then_RedirectionDecides()
	{
		using var env = WithEnv(("FORCE_COLOR", ""));

		new OutputOptions().IsAnsiEnabled(isStreamRedirected: true).Should().BeFalse();
		new OutputOptions().IsAnsiEnabled(isStreamRedirected: false).Should().BeTrue();
	}

	[TestMethod]
	[Description("FORCE_COLOR outranks NO_COLOR and TERM=dumb for colour, as it does on .NET 11.")]
	public void When_ForceColorMeetsNoColorOrDumbTerm_Then_ColourIsForced()
	{
		using (WithEnv(("FORCE_COLOR", "1"), ("NO_COLOR", "1")))
		{
			new OutputOptions().IsAnsiEnabled(isStreamRedirected: true).Should().BeTrue();
		}

		using (WithEnv(("FORCE_COLOR", "1"), ("TERM", "dumb")))
		{
			new OutputOptions().IsAnsiEnabled(isStreamRedirected: true).Should().BeTrue();
		}
	}

	[TestMethod]
	[Description("The application's own settings stay above every environment variable: AnsiMode.Never wins over FORCE_COLOR.")]
	public void When_AnsiModeIsNever_Then_ForceColorDoesNotColour()
	{
		using var env = WithEnv(("FORCE_COLOR", "1"));

		new OutputOptions { AnsiMode = AnsiMode.Never }.IsAnsiEnabled(isStreamRedirected: true).Should().BeFalse();
	}

	[TestMethod]
	[Description("Forcing colour on a redirected stream must not bring terminal control sequences (shell marks, progress) with it: they would land in the file or in the next program's input.")]
	public void When_ColourIsForcedOnARedirectedStream_Then_NoTerminalSequencesAreAllowed()
	{
		using (WithEnv(("FORCE_COLOR", "1")))
		{
			TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(new OutputOptions(), isStreamRedirected: true)
				.Should().BeFalse(because: "FORCE_COLOR asks for colour, not for control sequences");
		}

		using (WithEnv(("CLICOLOR_FORCE", "1")))
		{
			TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(new OutputOptions(), isStreamRedirected: true)
				.Should().BeFalse(because: "CLICOLOR_FORCE asks for colour, not for control sequences");
		}
	}

	[TestMethod]
	[Description("On a real terminal, terminal sequences stay allowed whether or not colour is forced.")]
	public void When_TheStreamIsATerminal_Then_TerminalSequencesAreAllowed()
	{
		using (WithEnv())
		{
			TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(new OutputOptions(), isStreamRedirected: false)
				.Should().BeTrue();
		}

		using (WithEnv(("FORCE_COLOR", "1")))
		{
			TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(new OutputOptions(), isStreamRedirected: false)
				.Should().BeTrue();
		}
	}

	[TestMethod]
	[Description("NO_COLOR and TERM=dumb keep their veto on terminal sequences even when FORCE_COLOR wins for colour.")]
	public void When_ForceColorMeetsNoColorOrDumbTerm_Then_TerminalSequencesStayOff()
	{
		using (WithEnv(("FORCE_COLOR", "1"), ("NO_COLOR", "1")))
		{
			TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(new OutputOptions(), isStreamRedirected: false)
				.Should().BeFalse();
		}

		using (WithEnv(("FORCE_COLOR", "1"), ("TERM", "dumb")))
		{
			TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(new OutputOptions(), isStreamRedirected: false)
				.Should().BeFalse();
		}
	}

	[TestMethod]
	[Description("Spectre styling follows the colour gate, so forced colour reaches it on a redirected stream where terminal sequences stay off.")]
	public void When_ColourIsForcedOnARedirectedStream_Then_StylingIsAllowedButSequencesAreNot()
	{
		using var env = WithEnv(("FORCE_COLOR", "1"));
		var options = new OutputOptions();

		TerminalAnsiCapability.IsAnsiCapableForColor(options, isStreamRedirected: true).Should().BeTrue();
		TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(options, isStreamRedirected: true).Should().BeFalse();
	}

	[TestMethod]
	[Description("AnsiMode.Always is an explicit choice by the application and keeps allowing terminal sequences on a redirected stream.")]
	public void When_AnsiModeIsAlways_Then_TerminalSequencesAreAllowedOnARedirectedStream()
	{
		using var env = WithEnv();

		TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(
				new OutputOptions { AnsiMode = AnsiMode.Always },
				isStreamRedirected: true)
			.Should().BeTrue();
	}

	[TestMethod]
	[Description("A hosted session that states its ANSI support decides for itself, ahead of FORCE_COLOR: an explicit yes carries sequences, an explicit no stops colour too.")]
	public void When_AHostedSessionStatesItsAnsiSupport_Then_ForceColorDoesNotOverrideIt()
	{
		using var env = WithEnv(("FORCE_COLOR", "1"));
		var options = new OutputOptions();

		using (ReplSessionIO.SetSession(output: TextWriter.Null, input: TextReader.Null, ansiMode: AnsiMode.Always))
		{
			TerminalAnsiCapability.IsAnsiCapableForColor(options, isStreamRedirected: true).Should().BeTrue();
			TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(options, isStreamRedirected: true).Should().BeTrue();
		}

		using (ReplSessionIO.SetSession(output: TextWriter.Null, input: TextReader.Null, ansiMode: AnsiMode.Never))
		{
			TerminalAnsiCapability.IsAnsiCapableForColor(options, isStreamRedirected: true).Should().BeFalse();
			TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(options, isStreamRedirected: true).Should().BeFalse();
		}
	}

	[TestMethod]
	[Description("A hosted client advertising ANSI through capability flags is coloured under FORCE_COLOR, yet NO_COLOR still vetoes the sequences it would otherwise get through the capability fallback.")]
	public void When_ForceColorMeetsNoColorInAHostedSession_Then_ColourIsOnAndSequencesAreOff()
	{
		using var env = WithEnv(("FORCE_COLOR", "1"), ("NO_COLOR", "1"));
		using var session = ReplSessionIO.SetSession(output: TextWriter.Null, input: TextReader.Null);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi;
		var options = new OutputOptions();

		TerminalAnsiCapability.IsAnsiCapableForColor(options, isStreamRedirected: true).Should().BeTrue();
		TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(options, isStreamRedirected: true).Should().BeFalse();
	}

	[TestMethod]
	[Description("AnsiMode.Never stays above FORCE_COLOR and the hosted capability fallback for both gates.")]
	public void When_AnsiModeIsNeverInAHostedSession_Then_NeitherGateOpens()
	{
		using var env = WithEnv(("FORCE_COLOR", "1"));
		using var session = ReplSessionIO.SetSession(output: TextWriter.Null, input: TextReader.Null);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi;
		var options = new OutputOptions { AnsiMode = AnsiMode.Never };

		TerminalAnsiCapability.IsAnsiCapableForColor(options, isStreamRedirected: true).Should().BeFalse();
		TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(options, isStreamRedirected: true).Should().BeFalse();
	}

	[TestMethod]
	[Description("AnsiMode.Always outranks NO_COLOR, so it keeps allowing terminal sequences on a redirected stream whatever the environment says.")]
	public void When_AnsiModeIsAlwaysAndNoColorIsSet_Then_TerminalSequencesAreStillAllowed()
	{
		using var env = WithEnv(("NO_COLOR", "1"));

		TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(
				new OutputOptions { AnsiMode = AnsiMode.Always },
				isStreamRedirected: true)
			.Should().BeTrue();
	}

	private static EnvironmentVariableScope WithEnv(params (string Name, string? Value)[] overrides) =>
		new([.. TerminalTestEnvironments.Neutral.Where(neutral => !overrides.Any(over => string.Equals(over.Name, neutral.Name, StringComparison.Ordinal))), .. overrides]);
}
