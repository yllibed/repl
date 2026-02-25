using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ConsoleLineReader_RemoteRendering
{
	[TestMethod]
	[Description("Remote key stream supports in-line insertion; rendered line and cursor position stay coherent.")]
	public async Task When_InsertingWithCursorMovement_Then_RenderedLineMatchesExpected()
	{
		var harness = new TerminalHarness(cols: 40, rows: 6);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.O, 'o'),
			Key(ConsoleKey.LeftArrow),
			Key(ConsoleKey.LeftArrow),
			Key(ConsoleKey.X, 'X'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			var result = await ConsoleLineReader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);

			result.Escaped.Should().BeFalse();
			result.Line.Should().Be("helXlo");
			harness.GetLine(0).Should().Contain("helXlo");
			harness.CursorX.Should().Be(0);
			harness.CursorY.Should().Be(1);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Backspace in the middle redraws trailing text correctly in remote rendering mode.")]
	public async Task When_BackspacingInMiddle_Then_RenderedLineStaysConsistent()
	{
		var harness = new TerminalHarness(cols: 40, rows: 6);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.O, 'o'),
			Key(ConsoleKey.LeftArrow),
			Key(ConsoleKey.LeftArrow),
			Key(ConsoleKey.Backspace),
			Key(ConsoleKey.Enter, '\r'),
		]);

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			var result = await ConsoleLineReader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);

			result.Escaped.Should().BeFalse();
			result.Line.Should().Be("helo");
			harness.GetLine(0).Should().Contain("helo");
			harness.CursorX.Should().Be(0);
			harness.CursorY.Should().Be(1);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0') =>
		new(ch, key, shift: false, alt: false, control: false);
}
