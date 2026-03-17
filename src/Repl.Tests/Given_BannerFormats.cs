namespace Repl.Tests;

[TestClass]
public sealed class Given_BannerFormats
{
	[TestMethod]
	[Description("BannerFormats should contain 'human' by default.")]
	public void When_Default_Then_ContainsHuman()
	{
		var options = new OutputOptions();

		options.BannerFormats.Should().Contain("human");
	}

	[TestMethod]
	[Description("BannerFormats should support adding custom format names.")]
	public void When_CustomFormatAdded_Then_ContainsCustomFormat()
	{
		var options = new OutputOptions();

		options.BannerFormats.Add("spectre");

		options.BannerFormats.Should().Contain("spectre");
		options.BannerFormats.Should().Contain("human");
	}

	[TestMethod]
	[Description("BannerFormats should be case-insensitive.")]
	public void When_CheckingCaseInsensitive_Then_Matches()
	{
		var options = new OutputOptions();

		options.BannerFormats.Should().Contain("HUMAN");
		options.BannerFormats.Should().Contain("Human");
	}

	[TestMethod]
	[Description("Removing 'human' from BannerFormats should exclude it.")]
	public void When_HumanRemoved_Then_NoLongerContainsHuman()
	{
		var options = new OutputOptions();

		options.BannerFormats.Remove("human");

		options.BannerFormats.Should().NotContain("human");
	}
}
