using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ConsoleLineReader
{
	[TestMethod]
	[Description("Redirected input returns the line from the reader.")]
	public async Task When_InputIsRedirected_Then_ReturnsLine()
	{
		var previousIn = Console.In;
		try
		{
			Console.SetIn(new StringReader("hello world"));
			var result = await ConsoleLineReader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
			result.Line.Should().Be("hello world");
			result.Escaped.Should().BeFalse();
		}
		finally
		{
			Console.SetIn(previousIn);
		}
	}

	[TestMethod]
	[Description("Redirected input returns null on EOF.")]
	public async Task When_InputIsRedirectedAndEof_Then_ReturnsNull()
	{
		var previousIn = Console.In;
		try
		{
			Console.SetIn(new StringReader(string.Empty));
			var result = await ConsoleLineReader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
			result.Line.Should().BeNull();
			result.Escaped.Should().BeFalse();
		}
		finally
		{
			Console.SetIn(previousIn);
		}
	}

	[TestMethod]
	[Description("Redirected input throws on pre-cancelled token.")]
	public async Task When_InputIsRedirectedAndTokenPreCancelled_Then_Throws()
	{
		var previousIn = Console.In;
		try
		{
			Console.SetIn(new StringReader("data"));
			using var cts = new CancellationTokenSource();
			await cts.CancelAsync().ConfigureAwait(false);

			var act = () => ConsoleLineReader.ReadLineAsync(cts.Token).AsTask();
			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		}
		finally
		{
			Console.SetIn(previousIn);
		}
	}
}
