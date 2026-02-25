namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ResultNavigation
{
	[TestMethod]
	[Description("Regression guard: verifies command returns navigate up result in interactive mode so that scope moves to parent after execution.")]
	public void When_ReturningNavigateUpInInteractiveMode_Then_ScopeMovesToParent()
	{
		var sut = ReplApp.Create()
			.Context("contact", contact =>
			{
				contact.Context("{id:int}", scoped =>
				{
					scoped.Map("show", () => Results.NavigateUp("done"));
				});
			});

		var output = ConsoleCaptureHelper.CaptureWithInput("show\nexit\n", () => sut.Run(["contact", "42"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("done");
		output.Text.Should().Contain("[contact]>");
	}

	[TestMethod]
	[Description("Regression guard: verifies command returns navigate up result in cli mode so that payload renders without interactive scope mutation.")]
	public void When_ReturningNavigateUpInCliMode_Then_PayloadIsRendered()
	{
		var sut = ReplApp.Create()
			.Context("contact", contact =>
			{
				contact.Context("{id:int}", scoped =>
				{
					scoped.Map("show", () => Results.NavigateUp("done"));
				});
			});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("done");
		output.Text.Should().NotContain("repl>");
	}

	[TestMethod]
	[Description("Regression guard: verifies command returns navigate to result in interactive mode so that prompt switches to target path.")]
	public void When_ReturningNavigateToInInteractiveMode_Then_ScopeMovesToTargetPath()
	{
		var sut = ReplApp.Create()
			.Context("contact", contact =>
			{
				contact.Context("{id:int}", scoped =>
				{
					scoped.Map("jump", () => Results.NavigateTo("contact", "moved"));
				});
			});

		var output = ConsoleCaptureHelper.CaptureWithInput("jump\nexit\n", () => sut.Run(["contact", "42"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("moved");
		output.Text.Should().Contain("[contact]>");
	}
}
