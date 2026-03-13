using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_RichPrompts
{
	[TestMethod]
	[Description("Choice menu renders all items and hint, Enter selects the default.")]
	public void When_ChoiceMenu_Enter_Then_SelectsDefault()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.Enter, '\r')]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadChoiceInteractiveSync(
			"Pick one", ["Alpha", "Bravo", "Charlie"], defaultIndex: 0, CancellationToken.None);

		result.Should().Be(0);

		// All three items should appear in the terminal viewport
		harness.Frames.Should().Contain(f =>
			f.Lines.Any(l => l.Contains("Alpha", StringComparison.Ordinal))
			&& f.Lines.Any(l => l.Contains("Bravo", StringComparison.Ordinal))
			&& f.Lines.Any(l => l.Contains("Charlie", StringComparison.Ordinal)));

		// Hint line should appear
		harness.Frames.Should().Contain(f =>
			f.Lines.Any(l => l.Contains("move", StringComparison.Ordinal)));
	}

	[TestMethod]
	[Description("Arrow down moves cursor to the next item.")]
	public void When_ChoiceMenu_DownArrow_Then_CursorMoves()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter, '\r')]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadChoiceInteractiveSync(
			"Pick one", ["Alpha", "Bravo", "Charlie"], defaultIndex: 0, CancellationToken.None);

		result.Should().Be(1);
		// After clearing the menu, the inline result should say "Bravo"
		harness.GetVisibleLines().Should().Contain(
			line => line.Contains("Bravo", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Arrow up wraps from first to last item.")]
	public void When_ChoiceMenu_UpArrowAtTop_Then_WrapsToBottom()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.UpArrow), Key(ConsoleKey.Enter, '\r')]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadChoiceInteractiveSync(
			"Pick one", ["Alpha", "Bravo", "Charlie"], defaultIndex: 0, CancellationToken.None);

		result.Should().Be(2);
	}

	[TestMethod]
	[Description("Shortcut key selects the matching item directly.")]
	public void When_ChoiceMenu_ShortcutKey_Then_SelectsMatchingItem()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.B, 'b')]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadChoiceInteractiveSync(
			"Pick one", ["Alpha", "Bravo", "Charlie"], defaultIndex: 0, CancellationToken.None);

		result.Should().Be(1);
	}

	[TestMethod]
	[Description("Esc cancels and returns -1.")]
	public void When_ChoiceMenu_Escape_Then_ReturnsMinus1()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.Escape)]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadChoiceInteractiveSync(
			"Pick one", ["Alpha", "Bravo"], defaultIndex: 0, CancellationToken.None);

		result.Should().Be(-1);
	}

	[TestMethod]
	[Description("Explicit mnemonic _Abort makes 'A' a shortcut.")]
	public void When_ChoiceMenu_ExplicitMnemonic_Then_ShortcutWorks()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.R, 'r')]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadChoiceInteractiveSync(
			"How to proceed?", ["_Abort", "_Retry", "_Fail"], defaultIndex: 0, CancellationToken.None);

		result.Should().Be(1);
		// Menu should have rendered underline ANSI for the mnemonic character
		harness.RawOutput.Should().MatchRegex(@"\u001b\[4m[A-Z]\u001b\[24m");
	}

	[TestMethod]
	[Description("Multi-choice renders checkboxes and Space toggles selection.")]
	public void When_MultiChoice_SpaceToggles_Then_SelectionChanges()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.Spacebar, ' '), Key(ConsoleKey.Enter, '\r')]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadMultiChoiceInteractiveSync(
			"Select", ["Auth", "Logging", "Cache"], defaults: [],
			minSelections: 0, maxSelections: null, CancellationToken.None);

		result.Should().Equal(0); // Only first item toggled on

		// Verify both checked and unchecked checkboxes rendered
		harness.Frames.Should().Contain(f =>
			f.Lines.Any(l => l.Contains("[x]", StringComparison.Ordinal))
			&& f.Lines.Any(l => l.Contains("[ ]", StringComparison.Ordinal)));
	}

	[TestMethod]
	[Description("Multi-choice with defaults shows pre-selected items.")]
	public void When_MultiChoice_WithDefaults_Then_PreSelected()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.Enter, '\r')]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadMultiChoiceInteractiveSync(
			"Select", ["Auth", "Logging", "Cache"], defaults: [0, 2],
			minSelections: 0, maxSelections: null, CancellationToken.None);

		result.Should().Equal(0, 2);
	}

	[TestMethod]
	[Description("Multi-choice Esc cancels and returns null.")]
	public void When_MultiChoice_Escape_Then_ReturnsNull()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.Escape)]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadMultiChoiceInteractiveSync(
			"Select", ["Auth", "Logging"], defaults: [],
			minSelections: 0, maxSelections: null, CancellationToken.None);

		result.Should().BeNull();
	}

	[TestMethod]
	[Description("Multi-choice enforces minimum selections.")]
	public void When_MultiChoice_BelowMin_Then_ShowsErrorAndRetries()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		// First Enter with 0 selected fails min=1, then Space+Enter succeeds
		var keys = new FakeKeyReader([
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.Enter, '\r'),
		]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadMultiChoiceInteractiveSync(
			"Select", ["Auth", "Logging"], defaults: [],
			minSelections: 1, maxSelections: null, CancellationToken.None);

		result.Should().ContainSingle();
		harness.RawOutput.Should().Contain("Please select at least 1 option(s).");
	}

	[TestMethod]
	[Description("Multi-choice Down+Space toggles second item.")]
	public void When_MultiChoice_NavigateAndToggle_Then_CorrectItemSelected()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([
			Key(ConsoleKey.DownArrow),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.Enter, '\r'),
		]);
		using var ctx = CreateContext(harness, keys);

		var result = ctx.Channel.ReadMultiChoiceInteractiveSync(
			"Select", ["Auth", "Logging", "Cache"], defaults: [],
			minSelections: 0, maxSelections: null, CancellationToken.None);

		result.Should().Equal(1);
	}

	[TestMethod]
	[Description("Choice menu items are separated from each other in terminal viewport.")]
	public void When_ChoiceMenu_Rendered_Then_EachItemOnOwnLine()
	{
		var harness = new TerminalHarness(cols: 60, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.Enter, '\r')]);
		using var ctx = CreateContext(harness, keys);

		ctx.Channel.ReadChoiceInteractiveSync(
			"Pick", ["Alpha", "Bravo", "Charlie"], defaultIndex: 0, CancellationToken.None);

		// Find a frame that has all 3 items visible
		var menuFrame = FindFrame(harness, "Alpha", "Bravo", "Charlie");
		menuFrame.Should().NotBeNull("all three items should appear in the terminal viewport");

		// Verify items are on separate lines
		var alphaLine = menuFrame!.Lines.First(l => l.Contains("Alpha", StringComparison.Ordinal));
		var bravoLine = menuFrame.Lines.First(l => l.Contains("Bravo", StringComparison.Ordinal));
		alphaLine.Should().NotContain("Bravo", "Alpha and Bravo should be on separate lines");
		bravoLine.Should().NotContain("Charlie", "Bravo and Charlie should be on separate lines");
	}

	[TestMethod]
	[Description("Hint line is on its own line, not mixed with menu items.")]
	public void When_ChoiceMenu_Rendered_Then_HintLineIsSeparate()
	{
		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keys = new FakeKeyReader([Key(ConsoleKey.Enter, '\r')]);
		using var ctx = CreateContext(harness, keys);

		ctx.Channel.ReadChoiceInteractiveSync(
			"Pick", ["Alpha", "Bravo"], defaultIndex: 0, CancellationToken.None);

		// Find a frame with both hint and menu items visible
		var menuFrame = FindFrame(harness, "move", "Alpha");
		menuFrame.Should().NotBeNull();

		// Hint should NOT be on the same line as any menu item
		var hintLine = menuFrame!.Lines.First(l => l.Contains("move", StringComparison.Ordinal));
		hintLine.Should().NotContain("Alpha");
		hintLine.Should().NotContain("Bravo");
	}

	// ---------- Helpers ----------

	private static TerminalHarness.TerminalFrame? FindFrame(
		TerminalHarness harness, params string[] requiredTexts)
	{
		return harness.Frames.FirstOrDefault(f =>
			requiredTexts.All(text =>
				f.Lines.Any(l => l.Contains(text, StringComparison.Ordinal))));
	}

	private static TestContext CreateContext(TerminalHarness harness, FakeKeyReader keyReader)
	{
		var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		ReplSessionIO.KeyReader = keyReader;
		ReplSessionIO.AnsiSupport = true;

		var outputOptions = new OutputOptions { AnsiMode = AnsiMode.Always };
		var interactionOptions = new InteractionOptions();
		var channel = new ConsoleInteractionChannel(interactionOptions, outputOptions);
		return new TestContext(channel, scope);
	}

	private sealed class TestContext(ConsoleInteractionChannel channel, IDisposable scope) : IDisposable
	{
		public ConsoleInteractionChannel Channel { get; } = channel;

		public void Dispose() => scope.Dispose();
	}

	private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0') =>
		new(ch, key, shift: false, alt: false, control: false);
}
