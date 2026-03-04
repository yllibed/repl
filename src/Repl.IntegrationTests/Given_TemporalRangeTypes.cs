namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_TemporalRangeTypes
{
	[TestMethod]
	[Description("Regression guard: verifies date range with start..end syntax binds as option parameter.")]
	public void When_UsingDateRangeWithDotDotSyntax_Then_RangeIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("report", (ReplDateRange period) =>
			$"{period.From:yyyy-MM-dd}|{period.To:yyyy-MM-dd}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["report", "--period", "2024-01-15..2024-02-15", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("2024-01-15|2024-02-15");
	}

	[TestMethod]
	[Description("Regression guard: verifies date range with start@duration syntax binds as option parameter.")]
	public void When_UsingDateRangeWithDurationSyntax_Then_RangeIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("report", (ReplDateRange period) =>
			$"{period.From:yyyy-MM-dd}|{period.To:yyyy-MM-dd}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["report", "--period", "2024-01-15@30d", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("2024-01-15|2024-02-14");
	}

	[TestMethod]
	[Description("Regression guard: verifies datetime range with start..end syntax binds as option parameter.")]
	public void When_UsingDateTimeRangeWithDotDotSyntax_Then_RangeIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("logs", (ReplDateTimeRange window) =>
			$"{window.From:HH:mm}|{window.To:HH:mm}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["logs", "--window", "2024-01-15T10:00..2024-01-15T18:00", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("10:00|18:00");
	}

	[TestMethod]
	[Description("Regression guard: verifies datetime range with start@duration syntax binds as option parameter.")]
	public void When_UsingDateTimeRangeWithDurationSyntax_Then_RangeIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("logs", (ReplDateTimeRange window) =>
			$"{window.From:HH:mm}|{window.To:HH:mm}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["logs", "--window", "2024-01-15T10:00@8h", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("10:00|18:00");
	}

	[TestMethod]
	[Description("Regression guard: verifies datetimeoffset range with start..end syntax binds as option parameter.")]
	public void When_UsingDateTimeOffsetRangeWithDotDotSyntax_Then_RangeIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("audit", (ReplDateTimeOffsetRange span) =>
			$"{span.From:HH:mm}|{span.To:HH:mm}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["audit", "--span", "2024-01-15T10:00+02:00..2024-01-15T18:00+02:00", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("10:00|18:00");
	}

	[TestMethod]
	[Description("Regression guard: verifies datetimeoffset range with start@duration syntax binds as option parameter.")]
	public void When_UsingDateTimeOffsetRangeWithDurationSyntax_Then_RangeIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("audit", (ReplDateTimeOffsetRange span) =>
			$"{span.From:HH:mm}|{span.To:HH:mm}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["audit", "--span", "2024-01-15T10:00+02:00@8h", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("10:00|18:00");
	}

	[TestMethod]
	[Description("Regression guard: verifies invalid date range literal produces an error.")]
	public void When_InvalidDateRangeLiteral_Then_InvocationFails()
	{
		var sut = ReplApp.Create();
		sut.Map("report", (ReplDateRange period) => "ok");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["report", "--period", "not-a-range", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("not a valid date range literal");
	}

	[TestMethod]
	[Description("Regression guard: verifies DateOnly range rejects sub-day durations.")]
	public void When_DateRangeDurationIsSubDay_Then_InvocationFails()
	{
		var sut = ReplApp.Create();
		sut.Map("report", (ReplDateRange period) => "ok");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["report", "--period", "2024-01-15@8h", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("whole days");
	}
}
