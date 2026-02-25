using System.Text;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ConsoleLineReader_HandleKey
{
	[TestMethod]
	[Description("Home key rewinds the cursor to column 0 and emits matching backspaces.")]
	public void When_HomePressed_Then_CursorMovesToStart()
	{
		var buffer = new StringBuilder("hello");
		var cursor = 5;
		var echo = new StringBuilder();

		var result = ConsoleLineReader.HandleKey(
			Key(ConsoleKey.Home),
			buffer,
			ref cursor,
			navigator: null,
			echo);

		result.Should().BeNull();
		cursor.Should().Be(0);
		echo.ToString().Should().Be("\b\b\b\b\b");
		buffer.ToString().Should().Be("hello");
	}

	[TestMethod]
	[Description("Inserting in the middle rewrites the suffix and restores cursor position.")]
	public void When_InsertInMiddle_Then_SuffixIsRewrittenAndCursorRestored()
	{
		var buffer = new StringBuilder("helo");
		var cursor = 2;
		var echo = new StringBuilder();

		var result = ConsoleLineReader.HandleKey(
			Key(ConsoleKey.L, 'l'),
			buffer,
			ref cursor,
			navigator: null,
			echo);

		result.Should().BeNull();
		buffer.ToString().Should().Be("hello");
		cursor.Should().Be(3);
		echo.ToString().Should().Be("llo\b\b");
	}

	[TestMethod]
	[Description("Backspace in the middle removes previous character and redraws trailing content.")]
	public void When_BackspaceInMiddle_Then_BufferShrinksAndEchoRedraws()
	{
		var buffer = new StringBuilder("hello");
		var cursor = 3;
		var echo = new StringBuilder();

		var result = ConsoleLineReader.HandleKey(
			Key(ConsoleKey.Backspace),
			buffer,
			ref cursor,
			navigator: null,
			echo);

		result.Should().BeNull();
		buffer.ToString().Should().Be("helo");
		cursor.Should().Be(2);
		echo.ToString().Should().Be("\blo \b\b\b");
	}

	[TestMethod]
	[Description("Delete in the middle removes current character and keeps cursor anchored.")]
	public void When_DeleteInMiddle_Then_BufferShrinksAndCursorStays()
	{
		var buffer = new StringBuilder("hello");
		var cursor = 2;
		var echo = new StringBuilder();

		var result = ConsoleLineReader.HandleKey(
			Key(ConsoleKey.Delete),
			buffer,
			ref cursor,
			navigator: null,
			echo);

		result.Should().BeNull();
		buffer.ToString().Should().Be("helo");
		cursor.Should().Be(2);
		echo.ToString().Should().Be("lo \b\b\b");
	}

	[TestMethod]
	[Description("History up/down navigation restores current draft entry after browsing history.")]
	public void When_HistoryUpAndDown_Then_DraftIsRestored()
	{
		var navigator = new HistoryNavigator(["first", "second"]);
		var buffer = new StringBuilder("dr");
		var cursor = 2;
		var echo = new StringBuilder();

		_ = ConsoleLineReader.HandleKey(Key(ConsoleKey.UpArrow), buffer, ref cursor, navigator, echo);
		_ = ConsoleLineReader.HandleKey(Key(ConsoleKey.UpArrow), buffer, ref cursor, navigator, echo);
		_ = ConsoleLineReader.HandleKey(Key(ConsoleKey.DownArrow), buffer, ref cursor, navigator, echo);
		_ = ConsoleLineReader.HandleKey(Key(ConsoleKey.DownArrow), buffer, ref cursor, navigator, echo);

		buffer.ToString().Should().Be("dr");
		cursor.Should().Be(2);
		echo.ToString().Should().Be(
			"\b\bsecond" +
			"\b\b\b\b\b\bfirst \b" +
			"\b\b\b\b\bsecond" +
			"\b\b\b\b\b\bdr    \b\b\b\b");
	}

	[TestMethod]
	[Description("Enter returns a completed read result and emits newline.")]
	public void When_EnterPressed_Then_ResultContainsLine()
	{
		var buffer = new StringBuilder("hello");
		var cursor = 5;
		var echo = new StringBuilder();

		var result = ConsoleLineReader.HandleKey(
			Key(ConsoleKey.Enter, '\r'),
			buffer,
			ref cursor,
			navigator: null,
			echo);

		result.Should().NotBeNull();
		result!.Value.Line.Should().Be("hello");
		result.Value.Escaped.Should().BeFalse();
		echo.ToString().Should().Be("\r\n");
	}

	private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0') =>
		new(ch, key, shift: false, alt: false, control: false);
}
