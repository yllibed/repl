using System.Threading.Channels;
using AwesomeAssertions;
using Repl.Remote;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_TerminalWriter
{
	[TestMethod]
	[DataRow(false, DisplayName = "Session cancellation")]
	[DataRow(true, DisplayName = "Output sink failure")]
	[Description("The output pump releases tokenless memory producers when its sole reader stops, without needing writer disposal to unblock the handler.")]
	public async Task When_ThePumpStops_Then_BlockedMemoryWritesAreReleased(bool failSink)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var lifetime = new CancellationTokenSource();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writer = new TerminalWriter(async (_, ct) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(ct).ConfigureAwait(false);
		}, lifetime);
		try
		{
			await writer.WriteAsync("first");
			await entered.Task.WaitAsync(deadline.Token);
			for (var i = 0; i < 256; i++) { await writer.WriteAsync("queued"); }
			var memory = writer.WriteAsync("blocked".AsMemory());
			memory.IsCompleted.Should().BeFalse();

			if (failSink) { release.TrySetException(new IOException("Output disconnected.")); }
			else { await lifetime.CancelAsync(); }

			var observe = () => memory.WaitAsync(deadline.Token);
			await observe.Should().ThrowAsync<ChannelClosedException>();
		}
		finally
		{
			release.TrySetResult();
			try { await writer.DisposeAsync(); }
			catch (Exception error) when (error is IOException or OperationCanceledException) { }
		}
	}

	[TestMethod]
	[DataRow(false, DisplayName = "Cancel a blocked line")]
	[DataRow(true, DisplayName = "Already cancelled line")]
	[Description("A command token cancels a memory line write under backpressure while leaving the session and output pump usable.")]
	public async Task When_OnlyTheCommandIsCancelled_Then_ItsLineIsNotQueued(bool cancelBeforeWrite)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var lifetime = new CancellationTokenSource();
		using var command = new CancellationTokenSource();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var output = new System.Text.StringBuilder();
		await using var writer = new TerminalWriter(async (text, ct) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(ct).ConfigureAwait(false);
			output.Append(text);
		}, lifetime);
		try
		{
			await writer.WriteAsync("first");
			await entered.Task.WaitAsync(deadline.Token);
			for (var i = 0; i < 256; i++) { await writer.WriteAsync("queued"); }
			if (cancelBeforeWrite) { await command.CancelAsync(); }
			var write = writer.WriteLineAsync("cancelled-line".AsMemory(), command.Token);

			await command.CancelAsync();
			var observe = () => write.WaitAsync(deadline.Token);
			await observe.Should().ThrowAsync<OperationCanceledException>().Where(error => error.CancellationToken == command.Token);
			lifetime.IsCancellationRequested.Should().BeFalse();
		}
		finally { release.TrySetResult(); }

		await writer.DisposeAsync();
		output.ToString().Should().NotContain("cancelled-line");
	}
}
