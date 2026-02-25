namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_IntegerLiteralParsing
{
	[TestMethod]
	[Description("Regression guard: verifies hexadecimal integer option is parsed so that C-like 0x literals are supported.")]
	public void When_ParsingHexadecimalInteger_Then_ValueIsConverted()
	{
		var sut = ReplApp.Create();
		sut.Map("calc", (int value) => value);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["calc", "--value", "0xFF", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("255");
	}

	[TestMethod]
	[Description("Regression guard: verifies binary integer suffix is parsed so that 011011b literals are supported.")]
	public void When_ParsingBinarySuffixInteger_Then_ValueIsConverted()
	{
		var sut = ReplApp.Create();
		sut.Map("calc", (int value) => value);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["calc", "--value", "011011b", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("27");
	}

	[TestMethod]
	[Description("Regression guard: verifies underscore separators are parsed so that C# style grouped integers are supported.")]
	public void When_ParsingIntegerWithUnderscores_Then_ValueIsConverted()
	{
		var sut = ReplApp.Create();
		sut.Map("calc", (long value) => value);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["calc", "--value", "1_000_000", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("1000000");
	}
}
