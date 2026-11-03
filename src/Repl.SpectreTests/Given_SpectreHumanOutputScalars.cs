using System.Globalization;

namespace Repl.SpectreTests;

[TestClass]
public sealed class Given_SpectreHumanOutputScalars
{
	[TestMethod]
	[Description("Dates remain literal values both alone and inside results; reflecting their Date properties would recurse indefinitely.")]
	public async Task When_DatesAreRendered_Then_TheyRemainScalars()
	{
		var transformer = new SpectreHumanOutputTransformer();
		var timestamp = new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero);
		object[] values = [timestamp, timestamp.UtcDateTime, TimeSpan.FromMinutes(5)];

		foreach (var value in values)
		{
			var expected = Convert.ToString(value, CultureInfo.InvariantCulture)!;
			var standalone = await transformer.TransformAsync(value).ConfigureAwait(false);
			var nested = await transformer.TransformAsync(new { Value = value }).ConfigureAwait(false);
			var table = await transformer.TransformAsync(new[] { new { Value = value } }).ConfigureAwait(false);

			standalone.Should().Be(expected);
			nested.Should().Contain(expected);
			table.Should().Contain(expected);
		}
	}
}
