using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_RunOptions
{
	[TestMethod]
	[Description("Regression guard: verifies a new run-options record leaves process-signal handling unspecified.")]
	public void When_CreatingRunOptions_Then_ProcessSignalHandlingIsNull()
	{
		var options = new ReplRunOptions();

		options.ProcessSignalHandling.Should().BeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies the zero value of the mode enum leaves signals to the caller. Enum zero is what an unset configuration field, a zero-initialized struct, or an explicit default() yields, so it must agree with the caller-owned application default rather than silently claiming process-wide signal ownership.")]
	public void When_UsingDefaultProcessSignalHandlingMode_Then_ValueIsNone()
	{
		default(ProcessSignalHandlingMode).Should().Be(ProcessSignalHandlingMode.None);
	}

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
