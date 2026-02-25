namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ResultSemantics
{
	[TestMethod]
	[Description("Regression guard: verifies handler returns text result so that exit code is zero.")]
	public void When_HandlerReturnsTextResult_Then_ExitCodeIsZero()
	{
		var sut = ReplApp.Create();
		sut.Map("ok", () => Results.Text("done"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["ok"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("done");
	}

	[TestMethod]
	[Description("Regression guard: verifies handler returns ok helper so that exit code is zero.")]
	public void When_HandlerReturnsOkResult_Then_ExitCodeIsZero()
	{
		var sut = ReplApp.Create();
		sut.Map("ok2", () => Results.Ok("done"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["ok2"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("done");
	}

	[TestMethod]
	[Description("Regression guard: verifies handler returns success helper so that success prefix is rendered and exit code is zero.")]
	public void When_HandlerReturnsSuccessResult_Then_ExitCodeIsZero()
	{
		var sut = ReplApp.Create();
		sut.Map("success", () => Results.Success("done"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["success"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Success: done");
	}

	[TestMethod]
	[Description("Regression guard: verifies handler returns error result so that exit code is one and error prefix is rendered.")]
	public void When_HandlerReturnsErrorResult_Then_ExitCodeIsOneAndErrorPrefixIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("fail", () => Results.Error("E_CONTACT", "Contact lookup failed."));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["fail"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Error: Contact lookup failed.");
	}

	[TestMethod]
	[Description("Regression guard: verifies handler returns validation result with details so that details are rendered.")]
	public void When_HandlerReturnsValidationResultWithDetails_Then_DetailsAreRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("validate", () => Results.Validation(
			"Invalid contact payload.",
			new ValidationDetails(Field: "email", Reason: "required")));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["validate"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation: Invalid contact payload.");
		output.Text.Should().Contain("Field : email");
		output.Text.Should().Contain("Reason: required");
	}

	[TestMethod]
	[Description("Regression guard: verifies handler returns not found result so that exit code is one and not found prefix is rendered.")]
	public void When_HandlerReturnsNotFoundResult_Then_ExitCodeIsOneAndNotFoundPrefixIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("missing", () => Results.NotFound("Contact '99' not found."));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["missing"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Not found: Contact '99' not found.");
	}

	[TestMethod]
	[Description("Regression guard: verifies handler throws exception so that framework emits structured execution error.")]
	public void When_HandlerThrowsException_Then_StructuredExecutionErrorIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("boom", static string () => throw new Exception("boom"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["boom", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Error: boom");
	}

	[TestMethod]
	[Description("Regression guard: verifies coded result in json mode so that machine-readable payload keeps code metadata.")]
	public void When_HandlerReturnsErrorResultInJsonMode_Then_CodeMetadataIsPreserved()
	{
		var sut = ReplApp.Create();
		sut.Map("failjson", () => Results.Error("E_CONTACT", "Contact lookup failed."));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["failjson", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("\"code\": \"E_CONTACT\"");
		output.Text.Should().Contain("\"kind\": \"error\"");
	}

	private sealed record ValidationDetails(string Field, string Reason);
}






