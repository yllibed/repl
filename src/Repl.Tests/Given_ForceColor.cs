using System.Reflection;
using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ForceColor
{
	[TestMethod]
	[DataRow("0")]
	[DataRow("false")]
	[DataRow("1")]
	[Description("Any non-empty FORCE_COLOR forces colour, including 0, as System.Console does on .NET 11.")]
	public void When_ForceColorHasAnyNonEmptyValue_Then_ColourIsForced(string value)
	{
		using var env = TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", value));

		new OutputOptions().IsColorEnabled(isStreamRedirected: true).Should().BeTrue();
	}

	[TestMethod]
	[Description("An empty FORCE_COLOR is ignored, so redirection decides as usual.")]
	public void When_ForceColorIsEmpty_Then_RedirectionDecides()
	{
		using var env = TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", ""));

		new OutputOptions().IsColorEnabled(isStreamRedirected: true).Should().BeFalse();
		new OutputOptions().IsColorEnabled(isStreamRedirected: false).Should().BeTrue();
	}

	[TestMethod]
	[Description("FORCE_COLOR outranks NO_COLOR and TERM=dumb for colour, as it does on .NET 11.")]
	public void When_ForceColorMeetsNoColorOrDumbTerm_Then_ColourIsForced()
	{
		using (TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"), ("NO_COLOR", "1")))
		{
			new OutputOptions().IsColorEnabled(isStreamRedirected: true).Should().BeTrue();
		}

		using (TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"), ("TERM", "dumb")))
		{
			new OutputOptions().IsColorEnabled(isStreamRedirected: true).Should().BeTrue();
		}
	}

	[TestMethod]
	[Description("The application's own settings stay above every environment variable: AnsiMode.Never wins over FORCE_COLOR.")]
	public void When_AnsiModeIsNever_Then_ForceColorDoesNotColour()
	{
		using var env = TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"));

		new OutputOptions { AnsiMode = AnsiMode.Never }.IsColorEnabled(isStreamRedirected: true).Should().BeFalse();
	}

	[TestMethod]
	[Description("Forcing colour on a redirected stream must not bring terminal control sequences (shell marks, progress) with it: they would land in the file or in the next program's input.")]
	public void When_ColourIsForcedOnARedirectedStream_Then_NoControlSequencesAreAllowed()
	{
		using (TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1")))
		{
			TerminalAnsiCapability.IsAnsiCapableForControlSequences(new OutputOptions(), isStreamRedirected: true)
				.Should().BeFalse(because: "FORCE_COLOR asks for colour, not for control sequences");
		}

		using (TerminalTestEnvironments.NeutralWith(("CLICOLOR_FORCE", "1")))
		{
			TerminalAnsiCapability.IsAnsiCapableForControlSequences(new OutputOptions(), isStreamRedirected: true)
				.Should().BeFalse(because: "CLICOLOR_FORCE asks for colour, not for control sequences");
		}
	}

	[TestMethod]
	[Description("On a real terminal, control sequences stay allowed whether or not colour is forced.")]
	public void When_TheStreamIsATerminal_Then_ControlSequencesAreAllowed()
	{
		using (TerminalTestEnvironments.NeutralWith())
		{
			TerminalAnsiCapability.IsAnsiCapableForControlSequences(new OutputOptions(), isStreamRedirected: false)
				.Should().BeTrue();
		}

		using (TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1")))
		{
			TerminalAnsiCapability.IsAnsiCapableForControlSequences(new OutputOptions(), isStreamRedirected: false)
				.Should().BeTrue();
		}
	}

	[TestMethod]
	[Description("NO_COLOR and TERM=dumb keep their veto on control sequences even when FORCE_COLOR wins for colour.")]
	public void When_ForceColorMeetsNoColorOrDumbTerm_Then_ControlSequencesStayOff()
	{
		using (TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"), ("NO_COLOR", "1")))
		{
			TerminalAnsiCapability.IsAnsiCapableForControlSequences(new OutputOptions(), isStreamRedirected: false)
				.Should().BeFalse();
		}

		using (TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"), ("TERM", "dumb")))
		{
			TerminalAnsiCapability.IsAnsiCapableForControlSequences(new OutputOptions(), isStreamRedirected: false)
				.Should().BeFalse();
		}
	}

	[TestMethod]
	[Description("A Repl.Spectre 0.12.0 binary styles its console through IsAnsiCapableForTerminalSequences(OutputOptions), which answered for colour in 0.12.0. The exact signature is bound by that compiled binary, so it is looked up by reflection, and it must keep the colour answer, or forced colour would not reach Spectre 0.12.0 running on this Repl.Core.")]
	public void When_ColourIsForcedWhereControlSequencesAreVetoed_Then_TheOldGateStillAnswersForColour()
	{
		// NO_COLOR keeps control sequences off on any stream, while FORCE_COLOR turns colour on.
		using var env = TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"), ("NO_COLOR", "1"));
		var options = new OutputOptions();
		var legacy = typeof(TerminalAnsiCapability).GetMethod(
			"IsAnsiCapableForTerminalSequences",
			BindingFlags.Public | BindingFlags.Static,
			binder: null,
			types: [typeof(OutputOptions)],
			modifiers: null);

		legacy.Should().NotBeNull("an already-compiled Repl.Spectre 0.12.0 binds to this signature");
		legacy!.ReturnType.Should().Be<bool>();
		TerminalAnsiCapability.IsAnsiCapableForControlSequences(options).Should().BeFalse();
		legacy.Invoke(obj: null, parameters: [options]).Should().BeOfType<bool>().Which.Should().BeTrue();
	}

	[TestMethod]
	[Description("A hosted session that states its ANSI support decides for itself, ahead of FORCE_COLOR: an explicit yes carries sequences, an explicit no stops colour too.")]
	public void When_AHostedSessionStatesItsAnsiSupport_Then_ForceColorDoesNotOverrideIt()
	{
		using var env = TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"));
		var options = new OutputOptions();

		using (ReplSessionIO.SetSession(output: TextWriter.Null, input: TextReader.Null, ansiMode: AnsiMode.Always))
		{
			TerminalAnsiCapability.IsAnsiCapableForColor(options, isStreamRedirected: true).Should().BeTrue();
			TerminalAnsiCapability.IsAnsiCapableForControlSequences(options, isStreamRedirected: true).Should().BeTrue();
		}

		using (ReplSessionIO.SetSession(output: TextWriter.Null, input: TextReader.Null, ansiMode: AnsiMode.Never))
		{
			TerminalAnsiCapability.IsAnsiCapableForColor(options, isStreamRedirected: true).Should().BeFalse();
			TerminalAnsiCapability.IsAnsiCapableForControlSequences(options, isStreamRedirected: true).Should().BeFalse();
		}
	}

	[TestMethod]
	[Description("A hosted client advertising ANSI through capability flags is coloured under FORCE_COLOR, yet NO_COLOR still vetoes the sequences it would otherwise get through the capability fallback.")]
	public void When_ForceColorMeetsNoColorInAHostedSession_Then_ColourIsOnAndSequencesAreOff()
	{
		using var env = TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"), ("NO_COLOR", "1"));
		using var session = ReplSessionIO.SetSession(output: TextWriter.Null, input: TextReader.Null);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi;
		var options = new OutputOptions();

		TerminalAnsiCapability.IsAnsiCapableForColor(options, isStreamRedirected: true).Should().BeTrue();
		TerminalAnsiCapability.IsAnsiCapableForControlSequences(options, isStreamRedirected: true).Should().BeFalse();
	}

	[TestMethod]
	[Description("AnsiMode.Never stays above FORCE_COLOR and the hosted capability fallback for both gates.")]
	public void When_AnsiModeIsNeverInAHostedSession_Then_NeitherGateOpens()
	{
		using var env = TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"));
		using var session = ReplSessionIO.SetSession(output: TextWriter.Null, input: TextReader.Null);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi;
		var options = new OutputOptions { AnsiMode = AnsiMode.Never };

		TerminalAnsiCapability.IsAnsiCapableForColor(options, isStreamRedirected: true).Should().BeFalse();
		TerminalAnsiCapability.IsAnsiCapableForControlSequences(options, isStreamRedirected: true).Should().BeFalse();
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("1")]
	[Description("AnsiMode.Always is an explicit choice by the application: it allows control sequences on a redirected stream, and outranks NO_COLOR doing so.")]
	public void When_AnsiModeIsAlways_Then_ControlSequencesAreAllowedOnARedirectedStream(string? noColor)
	{
		using var env = TerminalTestEnvironments.NeutralWith(("NO_COLOR", noColor));

		TerminalAnsiCapability.IsAnsiCapableForControlSequences(
				new OutputOptions { AnsiMode = AnsiMode.Always },
				isStreamRedirected: true)
			.Should().BeTrue();
	}
}
