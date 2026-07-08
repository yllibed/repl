namespace Repl.Tests;

[TestClass]
public sealed class Given_TerminalCapabilitiesClassifier
{
	[TestMethod]
	[Description("Known terminals with OSC progress support are tagged with ProgressReporting.")]
	public void When_IdentitySupportsProgress_Then_ProgressCapabilityIsInferred()
	{
		TerminalCapabilitiesClassifier.InferFromIdentity("Windows Terminal")
			.Should().HaveFlag(TerminalCapabilities.ProgressReporting);
		TerminalCapabilitiesClassifier.InferFromIdentity("wezterm")
			.Should().HaveFlag(TerminalCapabilities.ProgressReporting);
		TerminalCapabilitiesClassifier.InferFromIdentity("iTerm2")
			.Should().HaveFlag(TerminalCapabilities.ProgressReporting);
	}

	[TestMethod]
	[Description("Terminals known to render FinalTerm/VS Code semantic prompt marks are tagged with ShellIntegrationMarks.")]
	public void When_IdentitySupportsShellIntegration_Then_ShellIntegrationMarksCapabilityIsInferred()
	{
		TerminalCapabilitiesClassifier.InferFromIdentity("Windows Terminal")
			.Should().HaveFlag(TerminalCapabilities.ShellIntegrationMarks);
		TerminalCapabilitiesClassifier.InferFromIdentity("wezterm")
			.Should().HaveFlag(TerminalCapabilities.ShellIntegrationMarks);
		TerminalCapabilitiesClassifier.InferFromIdentity("iTerm2")
			.Should().HaveFlag(TerminalCapabilities.ShellIntegrationMarks);
		TerminalCapabilitiesClassifier.InferFromIdentity("ghostty")
			.Should().HaveFlag(TerminalCapabilities.ShellIntegrationMarks);
		TerminalCapabilitiesClassifier.InferFromIdentity("vscode")
			.Should().HaveFlag(TerminalCapabilities.ShellIntegrationMarks);
	}

	[TestMethod]
	[Description("A VS Code identity is recognized as a rich terminal (ANSI/VT input), not just an identity report, so hosted VS Code sessions get full capabilities.")]
	public void When_IdentityIsVsCode_Then_AnsiCapabilityIsInferred()
	{
		TerminalCapabilitiesClassifier.InferFromIdentity("vscode")
			.Should().HaveFlag(TerminalCapabilities.Ansi);
	}

	[TestMethod]
	[Description("ConEmu supports OSC 9;4 progress but not OSC 133 marks, so it must not be tagged with ShellIntegrationMarks.")]
	public void When_IdentityIsConEmu_Then_ShellIntegrationMarksCapabilityIsNotInferred()
	{
		TerminalCapabilitiesClassifier.InferFromIdentity("ConEmu")
			.Should().NotHaveFlag(TerminalCapabilities.ShellIntegrationMarks);
	}

	[TestMethod]
	[Description("A dumb terminal identity never advertises shell-integration marks.")]
	public void When_IdentityIsDumb_Then_ShellIntegrationMarksCapabilityIsNotInferred()
	{
		TerminalCapabilitiesClassifier.InferFromIdentity("dumb")
			.Should().NotHaveFlag(TerminalCapabilities.ShellIntegrationMarks);
	}
}
