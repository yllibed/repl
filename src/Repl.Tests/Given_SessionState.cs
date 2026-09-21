using System.Collections.Concurrent;

namespace Repl.Tests;

[TestClass]
public sealed class Given_SessionState
{
	private const int Writers = 8;
	private const int WritesPerWriter = 500;

	[TestMethod]
	[Description("Regression guard: IReplSessionState is resolved by every concurrent Telnet, WebSocket and MCP session, so its default implementation must tolerate concurrent writers. An unsynchronised Dictionary does not: concurrent Set can lose entries or corrupt the bucket table during a resize.")]
	public async Task When_SessionsWriteStateConcurrently_Then_EveryWriteIsReadableAfterwards()
	{
		var sut = new DefaultsSessionState();

		await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(() =>
		{
			for (var i = 0; i < WritesPerWriter; i++)
			{
				sut.Set(Key(writer, i), i);
			}
		})));

		for (var writer = 0; writer < Writers; writer++)
		{
			for (var i = 0; i < WritesPerWriter; i++)
			{
				sut.TryGet<int>(Key(writer, i), out var value).Should().BeTrue(
					"write {0} of writer {1} was lost", i, writer);
				value.Should().Be(i);
			}
		}
	}

	[TestMethod]
	[Description("Regression guard: reading session state while another session writes it must neither throw nor observe a torn entry. Enumeration-free readers still fault on a Dictionary being resized underneath them.")]
	public async Task When_OneSessionReadsWhileAnotherWrites_Then_NoReaderFaults()
	{
		var sut = new DefaultsSessionState();
		var faults = new ConcurrentQueue<Exception>();
		using var done = new CancellationTokenSource();

		var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
		{
			try
			{
				while (!done.Token.IsCancellationRequested)
				{
					sut.TryGet<int>(Key(0, 0), out _);
				}
			}
			catch (Exception exception)
			{
				faults.Enqueue(exception);
			}
		})).ToArray();

		var writer = Task.Run(() =>
		{
			for (var i = 0; i < Writers * WritesPerWriter; i++)
			{
				sut.Set(Key(0, i), i);
			}
		});

		await writer;
		await done.CancelAsync();
		await Task.WhenAll(readers);

		faults.Should().BeEmpty("a reader faulted while another session was writing");
	}

	private static string Key(int writer, int index) => $"key-{writer}-{index}";
}
