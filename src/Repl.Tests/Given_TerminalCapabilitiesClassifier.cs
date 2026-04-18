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
}
