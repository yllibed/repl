namespace Repl.SpectreTests;

[TestClass]
public sealed class Given_SpectreInteractionHandler
{
	[TestMethod]
	[Description("MapBackToOriginalIndex should return the correct index when a match is found.")]
	public void When_MapBackToOriginalIndex_WithMatch_Then_ReturnsCorrectIndex()
	{
		var choices = new[] { "_Abort", "_Retry", "_Fail" };

		var index = SpectreInteractionHandler.MapBackToOriginalIndex("Retry", choices);

		index.Should().Be(1);
	}

	[TestMethod]
	[Description("MapBackToOriginalIndex should return the first match index for the first choice.")]
	public void When_MapBackToOriginalIndex_WithFirstChoice_Then_ReturnsZero()
	{
		var choices = new[] { "_Abort", "_Retry", "_Fail" };

		var index = SpectreInteractionHandler.MapBackToOriginalIndex("Abort", choices);

		index.Should().Be(0);
	}

	[TestMethod]
	[Description("BUG: MapBackToOriginalIndex must return -1 (not 0) when no match is found, so the caller's Where(i => i >= 0) filter can exclude it.")]
	public void When_MapBackToOriginalIndex_WithNoMatch_Then_ReturnsNegativeOne()
	{
		var choices = new[] { "_Abort", "_Retry", "_Fail" };

		var index = SpectreInteractionHandler.MapBackToOriginalIndex("Unknown", choices);

		index.Should().Be(-1);
	}

	[TestMethod]
	[Description("MapBackToOriginalIndex should return -1 when the choices list is empty.")]
	public void When_MapBackToOriginalIndex_WithEmptyChoices_Then_ReturnsNegativeOne()
	{
		var choices = Array.Empty<string>();

		var index = SpectreInteractionHandler.MapBackToOriginalIndex("Anything", choices);

		index.Should().Be(-1);
	}

	[TestMethod]
	[Description("StripMnemonics should remove mnemonic underscores from choice labels.")]
	public void When_StripMnemonics_Then_MnemonicMarkersAreRemoved()
	{
		var choices = new[] { "_Abort", "_Retry", "_Fail" };

		var stripped = SpectreInteractionHandler.StripMnemonics(choices);

		stripped[0].Should().Be("Abort");
		stripped[1].Should().Be("Retry");
		stripped[2].Should().Be("Fail");
	}

	[TestMethod]
	[Description("StripMnemonics should return labels as-is when no mnemonic markers are present.")]
	public void When_StripMnemonics_WithNoMnemonics_Then_LabelsUnchanged()
	{
		var choices = new[] { "Save", "Cancel", "Quit" };

		var stripped = SpectreInteractionHandler.StripMnemonics(choices);

		stripped[0].Should().Be("Save");
		stripped[1].Should().Be("Cancel");
		stripped[2].Should().Be("Quit");
	}
}
