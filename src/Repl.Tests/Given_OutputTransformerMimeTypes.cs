namespace Repl.Tests;

[TestClass]
public sealed class Given_OutputTransformerMimeTypes
{
	[TestMethod]
	[Description("Verifies built-in output transformers advertise their produced MIME type.")]
	public void When_BuiltInTransformersAreRegistered_Then_MimeTypesAreAdvertised()
	{
		var options = new OutputOptions();

		options.Transformers["json"].MimeType.Should().Be("application/json");
		options.Transformers["xml"].MimeType.Should().Be("application/xml");
		options.Transformers["yaml"].MimeType.Should().Be("application/yaml");
		options.Transformers["markdown"].MimeType.Should().Be("text/markdown");
		options.Transformers["human"].MimeType.Should().Be("text/plain");
	}

	[TestMethod]
	[Description("Verifies custom output transformers default to text/plain unless they opt in.")]
	public void When_CustomTransformerDoesNotDeclareMimeType_Then_DefaultIsTextPlain()
	{
		IOutputTransformer transformer = new StubTransformer();

		transformer.MimeType.Should().Be("text/plain");
	}

	private sealed class StubTransformer : IOutputTransformer
	{
		public string Name => "stub";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(value?.ToString() ?? string.Empty);
	}
}
