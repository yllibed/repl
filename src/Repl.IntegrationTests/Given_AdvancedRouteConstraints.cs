namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_AdvancedRouteConstraints
{
	[TestMethod]
	[Description("Regression guard: verifies url and uri routes are both registered so that http input resolves to url route.")]
	public void When_InputIsHttpUrl_Then_UrlRouteIsSelected()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value:uri} kind", () => "uri");
		sut.Map("target {value:url} kind", () => "url");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["target", "https://example.com", "kind", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("url");
	}

	[TestMethod]
	[Description("Regression guard: verifies url constraint binds to Uri parameter so that handlers can consume strongly typed Uri values.")]
	public void When_UsingUrlConstraintWithUriParameter_Then_UriIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value:url} show", (Uri value) => value.Host);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["target", "https://example.com/path", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("example.com");
	}

	[TestMethod]
	[Description("Regression guard: verifies url and uri routes are both registered so that non-http uri input resolves to uri route.")]
	public void When_InputIsNonHttpUri_Then_UriRouteIsSelected()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value:uri} kind", () => "uri");
		sut.Map("target {value:url} kind", () => "url");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["target", "mailto:alice@example.com", "kind", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("uri");
	}

	[TestMethod]
	[Description("Regression guard: verifies uri constraint binds to Uri parameter so that non-http absolute URIs can be consumed as Uri.")]
	public void When_UsingUriConstraintWithUriParameter_Then_UriIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value:uri} show", (Uri value) => value.Scheme);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["target", "mailto:alice@example.com", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("mailto");
	}

	[TestMethod]
	[Description("Regression guard: verifies urn and uri routes are both registered so that urn input resolves to urn route.")]
	public void When_InputIsUrn_Then_UrnRouteIsSelected()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value:uri} kind", () => "uri");
		sut.Map("target {value:urn} kind", () => "urn");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["target", "urn:isbn:0451450523", "kind", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("urn");
	}

	[TestMethod]
	[Description("Regression guard: verifies urn constraint binds to Uri parameter so that urn literals can be consumed as Uri.")]
	public void When_UsingUrnConstraintWithUriParameter_Then_UriIsBound()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value:urn} show", (Uri value) => value.Scheme);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["target", "urn:isbn:0451450523", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("urn");
	}

	[TestMethod]
	[Description("Regression guard: verifies email and string routes are both registered so that practical email input resolves to email route.")]
	public void When_InputIsPracticalEmail_Then_EmailRouteIsSelected()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value}", () => "string");
		sut.Map("target {value:email}", () => "email");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["target", "alice@example.com", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("email");
	}

	[TestMethod]
	[Description("Regression guard: verifies practical email validation rejects display name input so that fallback route is used.")]
	public void When_InputContainsDisplayName_Then_EmailRouteIsNotSelected()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value}", () => "string");
		sut.Map("target {value:email}", () => "email");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["target", "Alice<alice@example.com>", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("string");
	}

	[TestMethod]
	[Description("Regression guard: verifies custom route constraint is registered so that runtime route matching uses the custom predicate.")]
	public void When_CustomConstraintIsRegistered_Then_RuntimeRouteMatchingUsesPredicate()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.AddRouteConstraint("slug", static value =>
				value.All(ch => char.IsLower(ch) || char.IsDigit(ch) || ch == '-')));
		sut.Map("article {id:slug} show", (string id) => id);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["article", "hello-world-1", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("hello-world-1");
	}

	[TestMethod]
	[Description("Regression guard: verifies unconstrained segment bound to Uri parameter infers uri constraint so that absolute uri input is accepted.")]
	public void When_UnconstrainedSegmentBindsToUriParameter_Then_UriConstraintIsInferred()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value} show", (Uri value) => value.Scheme);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["target", "mailto:alice@example.com", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("mailto");
	}

	[TestMethod]
	[Description("Regression guard: verifies unconstrained segment bound to Uri parameter infers uri constraint so that non-uri input is rejected at routing stage.")]
	public void When_UnconstrainedSegmentBindsToUriParameter_Then_NonUriInputIsRejected()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value} show", (Uri value) => value.Scheme);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["target", "not-a-uri", "show", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation:");
		output.Text.Should().Contain("parameter 'value'");
		output.Text.Should().Contain("expected: uri");
	}

	[TestMethod]
	[Description("Verifies optional argument omitted so that handler receives null.")]
	public void When_OptionalArgumentIsOmitted_Then_HandlerReceivesNull()
	{
		var sut = ReplApp.Create();
		sut.Map("contact add {name?}", (string? name) => name ?? "(none)");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["contact", "add", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("(none)");
	}

	[TestMethod]
	[Description("Verifies optional argument provided so that handler receives value.")]
	public void When_OptionalArgumentIsProvided_Then_HandlerReceivesValue()
	{
		var sut = ReplApp.Create();
		sut.Map("contact add {name?}", (string? name) => name ?? "(none)");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["contact", "add", "Alice", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Alice");
	}

	[TestMethod]
	[Description("Verifies optional constrained argument omitted so that handler receives null.")]
	public void When_OptionalConstrainedArgumentIsOmitted_Then_HandlerReceivesNull()
	{
		var sut = ReplApp.Create();
		sut.Map("contact add {name?} {email?:email}", (string? name, string? email) =>
			$"{name ?? "(no-name)"}|{email ?? "(no-email)"}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["contact", "add", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("(no-name)");
		output.Text.Should().Contain("(no-email)");
	}

	[TestMethod]
	[Description("Regression guard: verifies invalid email route value so that framework emits validation instead of unknown command.")]
	public void When_EmailConstraintValueIsInvalid_Then_FrameworkReturnsValidationError()
	{
		var sut = ReplApp.Create();
		sut.Map("add {name} {email:email}", (string name, string email) => $"{name}:{email}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["add", "test", "123", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation:");
		output.Text.Should().Contain("parameter 'email'");
		output.Text.Should().Contain("expected: email");
	}

	[TestMethod]
	[Description("Regression guard: verifies missing constrained argument so that framework emits explicit missing-parameter validation.")]
	public void When_EmailConstraintArgumentIsMissing_Then_FrameworkReturnsMissingParameterValidation()
	{
		var sut = ReplApp.Create();
		sut.Map("add {name} {email:email}", (string name, string email) => $"{name}:{email}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["add", "test", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation:");
		output.Text.Should().Contain("Missing value for parameter 'email'");
		output.Text.Should().Contain("expected: email");
	}

	[TestMethod]
	[Description("Regression guard: verifies missing multiple arguments so that framework emits explicit missing-parameters validation.")]
	public void When_MultipleRouteArgumentsAreMissing_Then_FrameworkReturnsMissingParametersValidation()
	{
		var sut = ReplApp.Create();
		sut.Map("add {name} {email:email}", (string name, string email) => $"{name}:{email}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["add", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation:");
		output.Text.Should().Contain("Missing values for parameters: name, email.");
	}
}
