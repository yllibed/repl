using System.Globalization;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_NumericParsingCulture
{
	[TestMethod]
	[Description("Regression guard: verifies invariant numeric parsing so that decimal with dot separator is accepted by default.")]
	public void When_NumericCultureIsInvariantByDefault_Then_DotDecimalIsParsed()
	{
		var sut = ReplApp.Create();
		sut.Map("calc", (decimal amount) => amount.ToString(CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["calc", "--amount", "12.5", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("12.5");
	}

	[TestMethod]
	[Description("Regression guard: verifies current culture numeric parsing so that decimal with comma separator is accepted when configured.")]
	public void When_NumericCultureIsCurrent_Then_CommaDecimalIsParsed()
	{
		var previousCulture = CultureInfo.CurrentCulture;
		var previousUiCulture = CultureInfo.CurrentUICulture;

		try
		{
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
			CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-CA");

			var sut = ReplApp.Create()
				.Options(options => options.Parsing.NumericCulture = NumericParsingCulture.Current);
			sut.Map("calc", (decimal amount) => amount.ToString(CultureInfo.CurrentCulture));

			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["calc", "--amount", "12,5", "--no-logo"]));

			output.ExitCode.Should().Be(0);
			output.Text.Should().Contain("12,5");
		}
		finally
		{
			CultureInfo.CurrentCulture = previousCulture;
			CultureInfo.CurrentUICulture = previousUiCulture;
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies invariant numeric parsing accepts underscore separators for decimal values.")]
	public void When_NumericCultureIsInvariant_Then_DecimalUnderscoreSeparatorsAreAccepted()
	{
		var sut = ReplApp.Create();
		sut.Map("calc", (decimal amount) => amount.ToString(CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["calc", "--amount", "1_234.5", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("1234.5");
	}

	[TestMethod]
	[Description("Regression guard: verifies current culture numeric parsing accepts underscore separators for decimal values.")]
	public void When_NumericCultureIsCurrent_Then_DecimalUnderscoreSeparatorsAreAccepted()
	{
		var previousCulture = CultureInfo.CurrentCulture;
		var previousUiCulture = CultureInfo.CurrentUICulture;

		try
		{
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
			CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-CA");

			var sut = ReplApp.Create()
				.Options(options => options.Parsing.NumericCulture = NumericParsingCulture.Current);
			sut.Map("calc", (decimal amount) => amount.ToString(CultureInfo.CurrentCulture));

			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["calc", "--amount", "1_234,5", "--no-logo"]));

			output.ExitCode.Should().Be(0);
			output.Text.Should().Contain("1234,5");
		}
		finally
		{
			CultureInfo.CurrentCulture = previousCulture;
			CultureInfo.CurrentUICulture = previousUiCulture;
		}
	}
}
