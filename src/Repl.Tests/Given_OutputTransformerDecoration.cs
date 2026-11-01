namespace Repl.Tests;

[TestClass]
public sealed class Given_OutputTransformerDecoration
{
	[TestMethod]
	[Description("Decorating a format keeps its existing CLI alias and banner eligibility while allowing the wrapper to delegate ordinary values.")]
	public async Task When_AFormatIsDecorated_Then_ItsRegistrationAndFallbackRemainUsable()
	{
		var options = new OutputOptions();
		options.DecorateTransformer("human", fallback => new PrefixOutput(fallback));

		var output = await options.Transformers["human"].TransformAsync("hello").ConfigureAwait(false);

		output.Should().Be("custom:hello");
		options.Aliases["human"].Should().Be("human");
		options.BannerFormats.Should().Contain("human");
	}

	[TestMethod]
	[Description("A failed decorator cannot erase the working transformer already registered for a format.")]
	public void When_ADecoratorReturnsNull_Then_TheOriginalTransformerIsRetained()
	{
		var options = new OutputOptions();
		var original = options.Transformers["human"];

		var decorate = () => options.DecorateTransformer("human", _ => null!);

		decorate.Should().Throw<InvalidOperationException>();
		options.Transformers["human"].Should().BeSameAs(original);
	}

	[TestMethod]
	[Description("A misspelled format fails during configuration instead of silently introducing another output format.")]
	public void When_TheFormatIsUnknown_Then_DecorationFails()
	{
		var options = new OutputOptions();

		var decorate = () => options.DecorateTransformer("missing", fallback => fallback);

		decorate.Should().Throw<ArgumentException>().WithParameterName("name");
	}

	private sealed class PrefixOutput(IOutputTransformer fallback) : IOutputTransformer
	{
		public string Name => fallback.Name;
		public async ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			"custom:" + await fallback.TransformAsync(value, cancellationToken).ConfigureAwait(false);
	}
}
