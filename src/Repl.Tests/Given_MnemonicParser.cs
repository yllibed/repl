namespace Repl.Tests;

[TestClass]
public sealed class Given_MnemonicParser
{
	[TestMethod]
	[DataRow("_Abort", "Abort", 'A')]
	[DataRow("No_thing", "Nothing", 't')]
	[DataRow("_Retry", "Retry", 'R')]
	[DataRow("_Fail", "Fail", 'F')]
	public void When_ExplicitMnemonic_Then_ShortcutIsExtracted(
		string label, string expectedDisplay, char expectedShortcut)
	{
		var (display, shortcut) = MnemonicParser.Parse(label);

		display.Should().Be(expectedDisplay);
		shortcut.Should().Be(expectedShortcut);
	}

	[TestMethod]
	public void When_DoubleUnderscore_Then_LiteralUnderscoreAndNoShortcut()
	{
		var (display, shortcut) = MnemonicParser.Parse("__real");

		display.Should().Be("_real");
		shortcut.Should().BeNull();
	}

	[TestMethod]
	public void When_NoUnderscore_Then_NoShortcut()
	{
		var (display, shortcut) = MnemonicParser.Parse("Plain");

		display.Should().Be("Plain");
		shortcut.Should().BeNull();
	}

	[TestMethod]
	public void When_TrailingUnderscore_Then_LiteralAndNoShortcut()
	{
		var (display, shortcut) = MnemonicParser.Parse("Test_");

		display.Should().Be("Test_");
		shortcut.Should().BeNull();
	}

	[TestMethod]
	public void When_EmptyLabel_Then_EmptyDisplayAndNoShortcut()
	{
		var (display, shortcut) = MnemonicParser.Parse("");

		display.Should().BeEmpty();
		shortcut.Should().BeNull();
	}

	[TestMethod]
	public void When_AssignShortcuts_WithExplicitMnemonics_Then_ExplicitHonored()
	{
		var labels = new[] { "_Abort", "_Retry", "_Fail" };
		var shortcuts = MnemonicParser.AssignShortcuts(labels);

		shortcuts[0].Should().Be('A');
		shortcuts[1].Should().Be('R');
		shortcuts[2].Should().Be('F');
	}

	[TestMethod]
	public void When_AssignShortcuts_WithoutMnemonics_Then_AutoAssigned()
	{
		var labels = new[] { "Save", "Cancel" };
		var shortcuts = MnemonicParser.AssignShortcuts(labels);

		shortcuts[0].Should().Be('S');
		shortcuts[1].Should().Be('C');
	}

	[TestMethod]
	public void When_AssignShortcuts_WithConflict_Then_FallsBackToOtherLetters()
	{
		// "Save" and "Skip" both start with 'S'
		var labels = new[] { "Save", "Skip", "Cancel" };
		var shortcuts = MnemonicParser.AssignShortcuts(labels);

		// First gets 'S', second tries 'S' (taken) then next letters
		shortcuts[0].Should().Be('S');
		shortcuts[1].Should().NotBe('S');
		shortcuts[1].Should().NotBeNull();
		shortcuts[2].Should().Be('C');
	}

	[TestMethod]
	public void When_AssignShortcuts_Mixed_Then_ExplicitTakesPriority()
	{
		var labels = new[] { "_Save", "Cancel" };
		var shortcuts = MnemonicParser.AssignShortcuts(labels);

		shortcuts[0].Should().Be('S');
		shortcuts[1].Should().Be('C');
	}

	[TestMethod]
	public void When_FormatAnsi_Then_ShortcutIsUnderlined()
	{
		var result = MnemonicParser.FormatAnsi("Abort", 'A');

		result.Should().Contain("\u001b[4m");
		result.Should().Contain("A");
		result.Should().Contain("\u001b[24m");
	}

	[TestMethod]
	public void When_FormatAnsi_NoShortcut_Then_LabelUnchanged()
	{
		var result = MnemonicParser.FormatAnsi("Plain", shortcut: null);

		result.Should().Be("Plain");
	}

	[TestMethod]
	public void When_FormatText_Then_ShortcutIsBracketed()
	{
		var result = MnemonicParser.FormatText("Abort", 'A');

		result.Should().Be("[A]bort");
	}

	[TestMethod]
	public void When_FormatText_MiddleShortcut_Then_BracketsInPlace()
	{
		var result = MnemonicParser.FormatText("Nothing", 't');

		result.Should().Be("No[t]hing");
	}

	[TestMethod]
	public void When_FormatText_DigitNotInDisplay_Then_PrefixedWithBrackets()
	{
		var result = MnemonicParser.FormatText("Save", '1');

		result.Should().Be("[1] Save");
	}

	[TestMethod]
	public void When_FormatText_NoShortcut_Then_LabelUnchanged()
	{
		var result = MnemonicParser.FormatText("Plain", shortcut: null);

		result.Should().Be("Plain");
	}

	[TestMethod]
	public void When_DoubleUnderscoreFollowedByMnemonic_Then_LiteralUnderscoreAndMnemonic()
	{
		var (display, shortcut) = MnemonicParser.Parse("__under_score");

		display.Should().Be("_underscore");
		shortcut.Should().Be('s');
	}
}
