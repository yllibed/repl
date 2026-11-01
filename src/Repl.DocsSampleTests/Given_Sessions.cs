using DocsMcpSample.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_Sessions
{
	[TestMethod]
	[Description("Connection timestamps follow the host clock and removing a session leaves other connections visible.")]
	public void When_ConnectionsChange_Then_MetadataUsesTheInjectedClock()
	{
		var clock = new FakeTimeProvider();
		var sessions = new Sessions(clock);
		var first = sessions.Add("websocket");
		clock.Advance(TimeSpan.FromMinutes(1));

		sessions.Add("local-cli");
		sessions.Remove(first);

		sessions.List().Should().ContainSingle().Which.ConnectedAt.Should().Be(clock.GetUtcNow());
	}
}
