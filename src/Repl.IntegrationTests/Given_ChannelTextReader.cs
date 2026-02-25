namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ChannelTextReader
{
	[TestMethod]
	[Description("LF-separated chunks are returned as individual lines by ReadLineAsync.")]
	public async Task When_EnqueueingLfSeparatedInput_Then_LinesAreReturnedInOrder()
	{
		var reader = new ChannelTextReader();
		reader.Enqueue("ping\nstatus\n");
		reader.Complete();

		var line1 = await reader.ReadLineAsync(CancellationToken.None);
		var line2 = await reader.ReadLineAsync(CancellationToken.None);
		var eof = await reader.ReadLineAsync(CancellationToken.None);

		line1.Should().Be("ping");
		line2.Should().Be("status");
		eof.Should().BeNull();
	}

	[TestMethod]
	[Description("CRLF-separated chunks are returned as individual lines by ReadLineAsync.")]
	public async Task When_EnqueueingCrLfSeparatedInput_Then_LinesAreReturnedInOrder()
	{
		var reader = new ChannelTextReader();
		reader.Enqueue("ping\r\nstatus\r\n");
		reader.Complete();

		var line1 = await reader.ReadLineAsync(CancellationToken.None);
		var line2 = await reader.ReadLineAsync(CancellationToken.None);
		var eof = await reader.ReadLineAsync(CancellationToken.None);

		line1.Should().Be("ping");
		line2.Should().Be("status");
		eof.Should().BeNull();
	}

	[TestMethod]
	[Description("CR-separated chunks are returned as individual lines by ReadLineAsync.")]
	public async Task When_EnqueueingCrSeparatedInput_Then_LinesAreReturnedInOrder()
	{
		var reader = new ChannelTextReader();
		reader.Enqueue("ping\rstatus\r");
		reader.Complete();

		var line1 = await reader.ReadLineAsync(CancellationToken.None);
		var line2 = await reader.ReadLineAsync(CancellationToken.None);
		var eof = await reader.ReadLineAsync(CancellationToken.None);

		line1.Should().Be("ping");
		line2.Should().Be("status");
		eof.Should().BeNull();
	}

	[TestMethod]
	[Description("Trailing text without a line terminator is returned after Complete().")]
	public async Task When_EnqueueingTextWithoutTerminator_Then_TextIsReturnedAfterComplete()
	{
		var reader = new ChannelTextReader();
		reader.Enqueue("ping");
		reader.Complete();

		var line = await reader.ReadLineAsync(CancellationToken.None);

		line.Should().Be("ping");
	}

	[TestMethod]
	[Description("ReadAsync returns raw chars for VtProbe-style char-level reads.")]
	public async Task When_ReadingCharsViaReadAsync_Then_RawCharsAreReturned()
	{
		var reader = new ChannelTextReader();
		reader.Enqueue("hello");
		reader.Complete();

		var buffer = new char[16];
		var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None);

		read.Should().Be(5);
		new string(buffer, 0, read).Should().Be("hello");
	}

	[TestMethod]
	[Description("A line split across two Enqueue calls is reassembled by ReadLineAsync.")]
	public async Task When_LineSpansMultipleChunks_Then_ReadLineAsyncReassembles()
	{
		var reader = new ChannelTextReader();
		reader.Enqueue("hel");
		reader.Enqueue("lo\n");
		reader.Complete();

		var line = await reader.ReadLineAsync(CancellationToken.None);

		line.Should().Be("hello");
	}

	[TestMethod]
	[Description("Complete during a blocking ReadLineAsync returns null.")]
	public async Task When_CompleteDuringBlockingReadLine_Then_ReturnsNull()
	{
		var reader = new ChannelTextReader();

		var readTask = reader.ReadLineAsync(CancellationToken.None);

		// Complete after a short delay so ReadLineAsync is already waiting.
		await Task.Delay(50);
		reader.Complete();

		var line = await readTask;
		line.Should().BeNull();
	}

	[TestMethod]
	[Description("ReadAsync returns 0 after Complete when no data remains.")]
	public async Task When_ReadAsyncAfterComplete_Then_ReturnsZero()
	{
		var reader = new ChannelTextReader();
		reader.Complete();

		var buffer = new char[16];
		var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None);

		read.Should().Be(0);
	}
}
