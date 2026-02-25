using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_RunOptions
{
	[TestMethod]
	[Description("Regression guard: verifies hosted-service lifecycle defaults to none so that runs avoid orchestration unless explicitly requested.")]
	public void When_CreatingRunOptions_Then_HostedServiceLifecycleDefaultsToNone()
	{
		var options = new ReplRunOptions();

		options.HostedServiceLifecycle.Should().Be(HostedServiceLifecycleMode.None);
	}

	[TestMethod]
	[Description("Regression guard: verifies guest lifecycle mode aliases none so that both semantic labels map to the same behavior.")]
	public void When_ComparingNoneAndGuest_Then_ValuesAreEquivalent()
	{
		((int)HostedServiceLifecycleMode.None).Should().Be((int)HostedServiceLifecycleMode.Guest);
	}

	[TestMethod]
	[Description("Regression guard: verifies terminal overrides are opt-in so default runs continue in auto-detection mode.")]
	public void When_CreatingRunOptions_Then_TerminalOverridesDefaultToNull()
	{
		var options = new ReplRunOptions();

		options.TerminalOverrides.Should().BeNull();
	}
}
