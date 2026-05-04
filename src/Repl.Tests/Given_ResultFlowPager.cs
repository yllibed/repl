using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ResultFlowPager
{
	[TestMethod]
	[Description("Result-flow pager advances by page on Space and stops on Q.")]
	public async Task When_PagingWithSpaceAndQuit_Then_WritesOnlyRequestedPages()
	{
		var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour\nfive",
			writer,
			keys,
			visibleRows: 2,
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("one");
		output.Should().Contain("two");
		output.Should().Contain("three");
		output.Should().Contain("four");
		output.Should().NotContain("five");
		output.Should().Contain("--More--");
	}

	[TestMethod]
	[Description("Result-flow pager advances by one line on Enter.")]
	public async Task When_PagingWithEnter_Then_AdvancesSingleLine()
	{
		var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Enter, '\r'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour",
			writer,
			keys,
			visibleRows: 2,
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("one");
		output.Should().Contain("two");
		output.Should().Contain("three");
		output.Should().NotContain("four");
	}

	private static ConsoleKeyInfo MakeKey(ConsoleKey key, char keyChar) =>
		new(keyChar, key, shift: false, alt: false, control: false);
}
