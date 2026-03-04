namespace Repl.Tests;

[TestClass]
public sealed class Given_TemporalRangeLiteralParser
{
	[TestMethod]
	[Description("Regression guard: verifies date range with start..end syntax parses correctly.")]
	public void When_DateRangeWithDotDotSyntax_Then_RangeIsParsed()
	{
		var result = TemporalRangeLiteralParser.TryParseDateRange("2024-01-15..2024-02-15", out var range);

		result.Should().BeTrue();
		range.From.Should().Be(new DateOnly(2024, 1, 15));
		range.To.Should().Be(new DateOnly(2024, 2, 15));
	}

	[TestMethod]
	[Description("Regression guard: verifies date range with start@duration syntax computes To correctly.")]
	public void When_DateRangeWithDurationSyntax_Then_ToIsComputed()
	{
		var result = TemporalRangeLiteralParser.TryParseDateRange("2024-01-15@30d", out var range);

		result.Should().BeTrue();
		range.From.Should().Be(new DateOnly(2024, 1, 15));
		range.To.Should().Be(new DateOnly(2024, 2, 14));
	}

	[TestMethod]
	[Description("Regression guard: verifies zero duration produces From == To.")]
	public void When_DateRangeWithZeroDuration_Then_FromEqualsTo()
	{
		var result = TemporalRangeLiteralParser.TryParseDateRange("2024-01-15@0d", out var range);

		result.Should().BeTrue();
		range.From.Should().Be(range.To);
	}

	[TestMethod]
	[Description("Regression guard: verifies reversed date range returns false.")]
	public void When_DateRangeIsReversed_Then_ReturnsFalse()
	{
		var result = TemporalRangeLiteralParser.TryParseDateRange("2024-02-15..2024-01-15", out _);

		result.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies invalid left part returns false.")]
	public void When_DateRangeHasInvalidLeft_Then_ReturnsFalse()
	{
		var result = TemporalRangeLiteralParser.TryParseDateRange("not-a-date..2024-01-15", out _);

		result.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies invalid right part returns false.")]
	public void When_DateRangeHasInvalidRight_Then_ReturnsFalse()
	{
		var result = TemporalRangeLiteralParser.TryParseDateRange("2024-01-15..not-a-date", out _);

		result.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies invalid duration returns false.")]
	public void When_DateRangeHasInvalidDuration_Then_ReturnsFalse()
	{
		var result = TemporalRangeLiteralParser.TryParseDateRange("2024-01-15@bad", out _);

		result.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies input without separator returns false.")]
	public void When_DateRangeHasNoSeparator_Then_ReturnsFalse()
	{
		var result = TemporalRangeLiteralParser.TryParseDateRange("2024-01-15", out _);

		result.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies empty string returns false.")]
	public void When_DateRangeIsEmpty_Then_ReturnsFalse()
	{
		var result = TemporalRangeLiteralParser.TryParseDateRange("", out _);

		result.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies datetime range with start..end syntax parses correctly.")]
	public void When_DateTimeRangeWithDotDotSyntax_Then_RangeIsParsed()
	{
		var result = TemporalRangeLiteralParser.TryParseDateTimeRange(
			"2024-01-15T10:00..2024-01-15T18:00", out var range);

		result.Should().BeTrue();
		range.From.Should().Be(new DateTime(2024, 1, 15, 10, 0, 0));
		range.To.Should().Be(new DateTime(2024, 1, 15, 18, 0, 0));
	}

	[TestMethod]
	[Description("Regression guard: verifies datetime range with start@duration syntax computes To correctly.")]
	public void When_DateTimeRangeWithDurationSyntax_Then_ToIsComputed()
	{
		var result = TemporalRangeLiteralParser.TryParseDateTimeRange(
			"2024-01-15T10:00@1h30m", out var range);

		result.Should().BeTrue();
		range.From.Should().Be(new DateTime(2024, 1, 15, 10, 0, 0));
		range.To.Should().Be(new DateTime(2024, 1, 15, 11, 30, 0));
	}

	[TestMethod]
	[Description("Regression guard: verifies reversed datetime range returns false.")]
	public void When_DateTimeRangeIsReversed_Then_ReturnsFalse()
	{
		var result = TemporalRangeLiteralParser.TryParseDateTimeRange(
			"2024-01-15T18:00..2024-01-15T10:00", out _);

		result.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies datetimeoffset range with start..end syntax parses correctly.")]
	public void When_DateTimeOffsetRangeWithDotDotSyntax_Then_RangeIsParsed()
	{
		var result = TemporalRangeLiteralParser.TryParseDateTimeOffsetRange(
			"2024-01-15T10:00+02:00..2024-01-15T18:00+02:00", out var range);

		result.Should().BeTrue();
		range.From.Should().Be(new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.FromHours(2)));
		range.To.Should().Be(new DateTimeOffset(2024, 1, 15, 18, 0, 0, TimeSpan.FromHours(2)));
	}

	[TestMethod]
	[Description("Regression guard: verifies datetimeoffset range with start@duration syntax computes To correctly.")]
	public void When_DateTimeOffsetRangeWithDurationSyntax_Then_ToIsComputed()
	{
		var result = TemporalRangeLiteralParser.TryParseDateTimeOffsetRange(
			"2024-01-15T10:00+02:00@8h", out var range);

		result.Should().BeTrue();
		range.From.Should().Be(new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.FromHours(2)));
		range.To.Should().Be(new DateTimeOffset(2024, 1, 15, 18, 0, 0, TimeSpan.FromHours(2)));
	}

	[TestMethod]
	[Description("Regression guard: verifies datetimeoffset range with UTC offsets parses correctly.")]
	public void When_DateTimeOffsetRangeWithUtcOffsets_Then_RangeIsParsed()
	{
		var result = TemporalRangeLiteralParser.TryParseDateTimeOffsetRange(
			"2024-01-15T10:00Z..2024-01-15T18:00Z", out var range);

		result.Should().BeTrue();
		range.From.Offset.Should().Be(TimeSpan.Zero);
		range.To.Offset.Should().Be(TimeSpan.Zero);
	}
}
