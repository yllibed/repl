namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ParameterSchemaBinding
{
	[TestMethod]
	[Description("Regression guard: verifies explicit short aliases are parsed through the command schema so single-dash tokens can bind typed handler parameters.")]
	public void When_UsingDeclaredShortAlias_Then_ParameterBindsSuccessfully()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"run",
			([ReplOption(Aliases = ["-m"])] string mode) => mode);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["run", "-m", "fast", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("fast");
	}

	[TestMethod]
	[Description("Regression guard: verifies route parameters cannot opt into option metadata so misconfigured handlers fail during command registration.")]
	public void When_RouteParameterDeclaresOptionAttribute_Then_MapFailsWithConfigurationError()
	{
		var sut = ReplApp.Create();

		var act = () => sut.Map(
			"show {id:int}",
			([ReplOption] int id) => id);

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Route parameter*cannot declare ReplOption/ReplArgument attributes*");
	}

	[TestMethod]
	[Description("Regression guard: verifies OptionOnly mode blocks positional fallback so handlers cannot accidentally consume unnamed user input.")]
	public void When_ParameterIsOptionOnlyAndOnlyPositionalValueIsProvided_Then_InvocationFails()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"set",
			([ReplOption(Mode = ReplParameterMode.OptionOnly)] int value) => value);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["set", "42", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Unable to bind parameter 'value'");
	}

	[TestMethod]
	[Description("Regression guard: verifies OptionAndPositional mode reports source conflicts so named and positional values cannot target the same parameter in one call.")]
	public void When_ParameterReceivesNamedAndPositionalValues_Then_InvocationFailsWithConflict()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"set",
			([ReplOption(Mode = ReplParameterMode.OptionAndPositional)] string value) => value);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["set", "--value", "alpha", "beta", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("cannot receive both named and positional values");
	}

	[TestMethod]
	[Description("Regression guard: verifies signed numeric literals remain positional so negative numbers are not misparsed as short options.")]
	public void When_PositionalValueIsSignedNumericLiteral_Then_ValueIsNotTreatedAsOptionToken()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", (double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "-1.5", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("-1.5");
	}
}
