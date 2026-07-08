namespace Repl.Tests;

[TestClass]
public sealed class Given_ShellIntegrationStatusAmbient
{
	[TestMethod]
	[Description("Disposing a scope restores the previously ambient slot, so a nested interactive session cannot leak its status into the outer one, and a scope closed in a synchronous flow leaves nothing behind for later reads.")]
	public void When_ScopeIsDisposed_Then_PreviousSlotIsRestored()
	{
		using var outer = ShellIntegrationStatusAmbient.Open();
		outer.Slot.Status = "OSC 133";

		using (var inner = ShellIntegrationStatusAmbient.Open())
		{
			inner.Slot.Status = "OSC 633 (VS Code)";
			ShellIntegrationStatusAmbient.Current.Should().Be("OSC 633 (VS Code)");
		}

		ShellIntegrationStatusAmbient.Current.Should().Be(
			"OSC 133",
			because: "the inner scope must restore what it shadowed instead of leaking its own slot");
	}

	[TestMethod]
	[Description("With no scope open, the ambient status reads null — the state IReplSessionInfo reports outside interactive prompt cycles.")]
	public void When_NoScopeIsOpen_Then_CurrentIsNull()
	{
		using (var scope = ShellIntegrationStatusAmbient.Open())
		{
			scope.Slot.Status = "OSC 133";
		}

		ShellIntegrationStatusAmbient.Current.Should().BeNull(
			because: "closing the only scope must leave no stale status behind");
	}
}
