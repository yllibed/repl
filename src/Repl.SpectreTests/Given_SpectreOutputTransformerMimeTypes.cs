namespace Repl.SpectreTests;

[TestClass]
public sealed class Given_SpectreOutputTransformerMimeTypes
{
	[TestMethod]
	[Description("Verifies the Spectre human transformer advertises text/plain by default.")]
	public void When_SpectreTransformerIsCreated_Then_MimeTypeIsTextPlain()
	{
		var transformer = new SpectreHumanOutputTransformer();

		transformer.MimeType.Should().Be("text/plain");
	}
}
