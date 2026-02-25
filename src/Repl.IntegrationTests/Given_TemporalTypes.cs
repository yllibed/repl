using System.Globalization;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_TemporalTypes
{
	[TestMethod]
	[Description("Regression guard: verifies date constraint binds to DateOnly so that ISO date route values are strongly typed.")]
	public void When_UsingDateConstraintWithDateOnlyParameter_Then_DateOnlyIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("day {value:date} show", (DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["day", "2026-02-19", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("2026-02-19");
	}

	[TestMethod]
	[Description("Regression guard: verifies date-only alias binds to DateOnly so that hyphenated date alias is supported.")]
	public void When_UsingDateOnlyAliasConstraint_Then_DateOnlyIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("day {value:date-only} show", (DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["day", "2026-02-19", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("2026-02-19");
	}

	[TestMethod]
	[Description("Regression guard: verifies compact dateonly alias binds to DateOnly so that non-hyphenated alias is supported.")]
	public void When_UsingDateOnlyCompactAliasConstraint_Then_DateOnlyIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("day {value:dateonly} show", (DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["day", "2026-02-19", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("2026-02-19");
	}

	[TestMethod]
	[Description("Regression guard: verifies datetime constraint binds to DateTime so that local datetime literals are converted.")]
	public void When_UsingDateTimeConstraintWithDateTimeParameter_Then_DateTimeIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("stamp {value:datetime} show", (DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["stamp", "2026-02-19T14:30:15", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("2026-02-19T14:30:15");
	}

	[TestMethod]
	[Description("Regression guard: verifies datetime constraint accepts space separator so that pragmatic local datetime input is converted.")]
	public void When_UsingDateTimeConstraintWithSpaceSeparatedLiteral_Then_DateTimeIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("stamp {value:datetime} show", (DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["stamp", "2026-02-19 14:30:15", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("2026-02-19T14:30:15");
	}

	[TestMethod]
	[Description("Regression guard: verifies time constraint binds to TimeOnly so that HH:mm literals are converted.")]
	public void When_UsingTimeConstraintWithTimeOnlyParameter_Then_TimeOnlyIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("clock {value:time} show", (TimeOnly value) => value.ToString("HH:mm", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["clock", "09:45", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("09:45");
	}

	[TestMethod]
	[Description("Regression guard: verifies compact timeonly alias binds to TimeOnly so that non-hyphenated alias is supported.")]
	public void When_UsingTimeOnlyCompactAliasConstraint_Then_TimeOnlyIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("clock {value:timeonly} show", (TimeOnly value) => value.ToString("HH:mm", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["clock", "09:45", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("09:45");
	}

	[TestMethod]
	[Description("Regression guard: verifies datetimeoffset constraint binds to DateTimeOffset so that explicit offset literals are converted.")]
	public void When_UsingDateTimeOffsetConstraintWithParameter_Then_DateTimeOffsetIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("when {value:datetimeoffset} show", (DateTimeOffset value) => value.ToString("zzz", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["when", "2026-02-19T14:30:15+02:00", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("+02:00");
	}

	[TestMethod]
	[Description("Regression guard: verifies datetimeoffset constraint accepts space separator so that offset literal with space is converted.")]
	public void When_UsingDateTimeOffsetConstraintWithSpaceSeparatedLiteral_Then_DateTimeOffsetIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("when {value:datetimeoffset} show", (DateTimeOffset value) => value.ToString("zzz", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["when", "2026-02-19 14:30:15+02:00", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("+02:00");
	}

	[TestMethod]
	[Description("Regression guard: verifies timespan compact literal binds to TimeSpan so that human-friendly duration can be parsed.")]
	public void When_UsingTimeSpanConstraintWithCompactLiteral_Then_TimeSpanIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value:timespan} show", (TimeSpan value) => (int)value.TotalMinutes);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "1h30", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("90");
	}

	[TestMethod]
	[Description("Regression guard: verifies time-span alias binds to TimeSpan so that hyphenated alias remains supported in route templates.")]
	public void When_UsingTimeSpanAliasConstraint_Then_TimeSpanIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value:time-span} show", (TimeSpan value) => (int)value.TotalMinutes);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "1h30", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("90");
	}

	[TestMethod]
	[Description("Regression guard: verifies timespan supports millisecond and underscore literals so that compact machine-friendly values can be parsed.")]
	public void When_UsingTimeSpanConstraintWithMillisecondsAndUnderscores_Then_TimeSpanIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value:timespan} show", (TimeSpan value) => (int)value.TotalMilliseconds);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "1_250ms", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("1250");
	}

	[TestMethod]
	[Description("Regression guard: verifies timespan supports whitespace-separated compact literals so that user-friendly values like '1h 30' are parsed.")]
	public void When_UsingTimeSpanConstraintWithWhitespaceCompactLiteral_Then_TimeSpanIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value:timespan} show", (TimeSpan value) => (int)value.TotalMinutes);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "1h 30", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("90");
	}

	[TestMethod]
	[Description("Regression guard: verifies timespan supports signed compact literals so that negative durations are parsed.")]
	public void When_UsingTimeSpanConstraintWithSignedCompactLiteral_Then_TimeSpanIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value:timespan} show", (TimeSpan value) => (int)value.TotalMinutes);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "-5m", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("-5");
	}

	[TestMethod]
	[Description("Regression guard: verifies timespan supports h:m:s style literals so that lenient input is accepted.")]
	public void When_UsingTimeSpanConstraintWithColonLiteral_Then_TimeSpanIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value:timespan} show", (TimeSpan value) => (int)value.TotalSeconds);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "1:2:3", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("3723");
	}

	[TestMethod]
	[Description("Regression guard: verifies timespan supports ISO-8601 duration literals so that PT1H30M is parsed.")]
	public void When_UsingTimeSpanConstraintWithIso8601Literal_Then_TimeSpanIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value:timespan} show", (TimeSpan value) => (int)value.TotalMinutes);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "PT1H30M", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("90");
	}

	[TestMethod]
	[Description("Regression guard: verifies unconstrained segment bound to TimeSpan infers timespan constraint so that compact duration is accepted.")]
	public void When_UnconstrainedSegmentBindsToTimeSpanParameter_Then_TimeSpanConstraintIsInferred()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value} show", (TimeSpan value) => (int)value.TotalSeconds);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "5m", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("300");
	}

	[TestMethod]
	[Description("Regression guard: verifies unconstrained segment bound to DateOnly infers date constraint so that ISO date input is accepted.")]
	public void When_UnconstrainedSegmentBindsToDateOnlyParameter_Then_DateConstraintIsInferred()
	{
		var sut = ReplApp.Create();
		sut.Map("day {value} show", (DateOnly value) => value.Day);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["day", "2026-02-19", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("19");
	}

	[TestMethod]
	[Description("Regression guard: verifies unconstrained segment bound to DateTime infers datetime constraint so that ISO local datetime input is accepted.")]
	public void When_UnconstrainedSegmentBindsToDateTimeParameter_Then_DateTimeConstraintIsInferred()
	{
		var sut = ReplApp.Create();
		sut.Map("stamp {value} show", (DateTime value) => value.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["stamp", "2026-02-19T14:30:15", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("14:30:15");
	}

	[TestMethod]
	[Description("Regression guard: verifies unconstrained segment bound to TimeOnly infers time constraint so that HH:mm input is accepted.")]
	public void When_UnconstrainedSegmentBindsToTimeOnlyParameter_Then_TimeConstraintIsInferred()
	{
		var sut = ReplApp.Create();
		sut.Map("clock {value} show", (TimeOnly value) => value.ToString("HH:mm", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["clock", "09:45", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("09:45");
	}

	[TestMethod]
	[Description("Regression guard: verifies unconstrained segment bound to DateTimeOffset infers datetimeoffset constraint so that offset literal input is accepted.")]
	public void When_UnconstrainedSegmentBindsToDateTimeOffsetParameter_Then_DateTimeOffsetConstraintIsInferred()
	{
		var sut = ReplApp.Create();
		sut.Map("when {value} show", (DateTimeOffset value) => value.Offset.ToString());

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["when", "2026-02-19T14:30:00+02:00", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("02:00:00");
	}

	[TestMethod]
	[Description("Regression guard: verifies unconstrained segment bound to TimeSpan infers timespan constraint so that non-duration input is rejected at routing stage.")]
	public void When_UnconstrainedSegmentBindsToTimeSpanParameter_Then_InvalidDurationIsRejected()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value} show", (TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "tomorrow", "show", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation:");
		output.Text.Should().Contain("parameter 'value'");
		output.Text.Should().Contain("expected: timespan");
	}

	[TestMethod]
	[Description("Regression guard: verifies date constraint rejects non-ISO date so that invalid date input does not match the route.")]
	public void When_UsingDateConstraintWithInvalidDateLiteral_Then_InputIsRejected()
	{
		var sut = ReplApp.Create();
		sut.Map("day {value:date} show", (DateOnly value) => value.Day);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["day", "2026/02/19", "show", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation:");
		output.Text.Should().Contain("parameter 'value'");
		output.Text.Should().Contain("expected: date");
	}

	[TestMethod]
	[Description("Regression guard: verifies timespan constraint rejects malformed ISO duration so that invalid duration input does not match the route.")]
	public void When_UsingTimeSpanConstraintWithMalformedIsoLiteral_Then_InputIsRejected()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value:timespan} show", (TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "PT", "show", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation:");
		output.Text.Should().Contain("parameter 'value'");
		output.Text.Should().Contain("expected: timespan");
	}
}
