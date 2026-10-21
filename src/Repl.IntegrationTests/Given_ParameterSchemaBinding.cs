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
	[Description("Verifies a parameter Repl supplies itself cannot declare option metadata: it is never an option, so the attribute would do nothing and command registration fails instead of silently ignoring it.")]
	[DataRow("progress", "ReplOption", DisplayName = "Structured progress with ReplOption")]
	[DataRow("percentage", "ReplArgument", DisplayName = "Percentage progress with ReplArgument")]
	[DataRow("channel", "ReplOption", DisplayName = "Interaction channel with ReplOption")]
	[DataRow("cancellation", "ReplOption", DisplayName = "Cancellation token with ReplOption")]
	public void When_AReplSuppliedParameterDeclaresOptionMetadata_Then_MapFailsWithConfigurationError(string parameter, string attribute)
	{
		var sut = ReplApp.Create();

		Action act = parameter switch
		{
			"progress" => () => sut.Map("sync", ([ReplOption(AutomationHidden = true)] IProgress<ReplProgressEvent> progress) => "ok"),
			"percentage" => () => sut.Map("sync", ([ReplArgument] IProgress<double> progress) => "ok"),
			"channel" => () => sut.Map("sync", ([ReplOption] IReplInteractionChannel channel) => "ok"),
			"cancellation" => () => sut.Map("sync", ([ReplOption] CancellationToken cancellationToken) => "ok"),
			_ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "Unknown test row."),
		};

		act.Should().Throw<InvalidOperationException>()
			.WithMessage($"*is supplied by Repl*[{attribute}]*");
	}

	[TestMethod]
	[Description("Verifies an options-group property cannot share its name with a parameter Repl supplies: values are matched to parameters by name, ignoring case, so the option's value would be bound to the supplied parameter.")]
	public void When_AnOptionSharesItsNameWithAReplSuppliedParameter_Then_MapFailsWithConfigurationError()
	{
		var sut = ReplApp.Create();

		var act = () => sut.Map("sync", (ProgressNamedOptions options, IProgress<double> progress) => options.Progress);

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*'Progress'*parameter 'progress' that Repl supplies*");
	}

	[TestMethod]
	[Description("Verifies a parameter of a type Repl supplies that binding resolves before matching any value by name, bound explicitly from services or the cancellation token, may share its name with an option, and both receive their own value.")]
	[DataRow("services", DisplayName = "Bound explicitly from services")]
	[DataRow("cancellation", DisplayName = "The cancellation token")]
	public void When_AParameterResolvedBeforeNamesSharesItsNameWithAnOption_Then_BothBind(string parameter)
	{
		var sut = ReplApp.Create();
		_ = parameter switch
		{
			"services" => sut.Map("sync", (ProgressNamedOptions options, [FromServices] IReplInteractionChannel progress) =>
				progress is not null && options.Progress ? "bound" : "unbound"),
			"cancellation" => sut.Map("sync", (CancelNamedOptions options, CancellationToken cancel) =>
				options.Cancel && !cancel.IsCancellationRequested ? "bound" : "unbound"),
			_ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "Unknown test row."),
		};
		var option = string.Equals(parameter, "services", StringComparison.Ordinal) ? "--progress" : "--cancel";

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["sync", option, "--no-logo"]));

		output.ExitCode.Should().Be(0, output.Text);
		output.Text.Should().Contain("bound").And.NotContain("unbound");
	}

	[ReplOptionsGroup]
	public sealed class CancelNamedOptions
	{
		public bool Cancel { get; set; }
	}

	[ReplOptionsGroup]
	public sealed class ProgressNamedOptions
	{
		public bool Progress { get; set; }
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

		output.ExitCode.Should().Be(2);
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

		output.ExitCode.Should().Be(2);
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
