namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ReplSessionIO
{
	[TestMethod]
	[Description("Capability bits earned only by identity inference are replaced when the client re-identifies: a Windows Terminal → dumb downgrade clears ShellIntegrationMarks (and the other inferred flags) instead of keeping them forever.")]
	public void When_TerminalIdentityDowngrades_Then_InferredCapabilitiesAreReplaced()
	{
		using var session = ReplSessionIO.SetSession(new StringWriter(), TextReader.Null);

		ReplSessionIO.TerminalIdentity = "Windows Terminal";
		var advertised = ReplSessionIO.TerminalCapabilities;
		ReplSessionIO.TerminalIdentity = "dumb";
		var downgraded = ReplSessionIO.TerminalCapabilities;

		advertised.Should().HaveFlag(TerminalCapabilities.ShellIntegrationMarks);
		downgraded.Should().NotHaveFlag(TerminalCapabilities.ShellIntegrationMarks);
		downgraded.Should().NotHaveFlag(TerminalCapabilities.Ansi);
		downgraded.Should().HaveFlag(TerminalCapabilities.IdentityReporting);
	}

	[TestMethod]
	[Description("Capabilities granted explicitly (overrides, control messages) are not revoked by a later identity downgrade — only the portion the previous identity inference earned is recalculated.")]
	public void When_TerminalIdentityDowngrades_Then_ExplicitlyGrantedCapabilitiesSurvive()
	{
		using var session = ReplSessionIO.SetSession(new StringWriter(), TextReader.Null);

		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.VtInput;
		ReplSessionIO.TerminalIdentity = "Windows Terminal";
		ReplSessionIO.TerminalIdentity = "dumb";

		ReplSessionIO.TerminalCapabilities.Should().HaveFlag(
			TerminalCapabilities.VtInput,
			because: "the explicit grant predates the inference and must survive the downgrade");
		ReplSessionIO.TerminalCapabilities.Should().NotHaveFlag(TerminalCapabilities.ShellIntegrationMarks);
	}

	[TestMethod]
	[Description("A hosted transport's identity update (Telnet TTYPE / control-message path) follows the same replace-inferred rule as the ambient setter, so the downgrade behavior covers both write sites.")]
	public async Task When_HostIdentityDowngrades_Then_InferredCapabilitiesAreReplaced()
	{
		await using var host = new StreamedReplHost(new StringWriter());

		host.UpdateTerminalIdentity("Windows Terminal");
		host.UpdateTerminalIdentity("dumb");

		ReplSessionIO.TryGetSession(host.SessionId, out var session).Should().BeTrue();
		session.TerminalCapabilities.Should().NotHaveFlag(TerminalCapabilities.ShellIntegrationMarks);
		session.TerminalIdentity.Should().Be("dumb");
	}
}
