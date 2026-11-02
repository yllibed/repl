using AwesomeAssertions;
using Repl.Samples;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_SampleRemoteClient
{
	[TestMethod]
	[Description("An absent server produces a diagnostic naming the pipe and a recovery action instead of a generic cancellation error.")]
	public async Task When_NoServerListens_Then_TheConnectionTimeoutIdentifiesThePipe()
	{
		var pipe = "missing-" + Guid.NewGuid().ToString("N");

		var connect = () => RemoteClient.ConnectAsync(pipe, CancellationToken.None, TimeSpan.FromMilliseconds(30));

		await connect.Should().ThrowAsync<TimeoutException>().WithMessage("*" + pipe + "*Start the server*").ConfigureAwait(false);
	}

	[TestMethod]
	[Description("A caller cancellation remains cancellation rather than being misreported as an absent server.")]
	public async Task When_TheCallerCancels_Then_TheConnectionIsNotReportedAsATimeout()
	{
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync().ConfigureAwait(false);

		var connect = () => RemoteClient.ConnectAsync("missing-" + Guid.NewGuid().ToString("N"), cancellation.Token);

		await connect.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
	}
}
