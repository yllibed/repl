using AwesomeAssertions;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ContextValidationAndDeeplinking
{
	[TestMethod]
	[Description("Regression guard: verifies context validation fails so that handler is not invoked and exit code is non zero.")]
	public void When_ContextValidationFails_Then_HandlerIsNotInvokedAndExitCodeIsNonZero()
	{
		var handlerCalled = false;
		var sut = ReplApp.Create();
		sut.Context("contact", contact =>
		{
			contact.Context("{id:int}",
				scope =>
				{
					scope.Map("show", (int id) =>
					{
						handlerCalled = true;
						return id;
					});
				},
				validation: (int id) => id == 42);
		});

		var exitCode = sut.Run(["contact", "99", "show"]);

		exitCode.Should().Be(1);
		handlerCalled.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies context validation passes so that handler is invoked.")]
	public void When_ContextValidationPasses_Then_HandlerIsInvoked()
	{
		var handlerCalled = false;
		var sut = ReplApp.Create();
		sut.Context("contact", contact =>
		{
			contact.Context("{id:int}",
				scope =>
				{
					scope.Map("show", (int id) =>
					{
						handlerCalled = true;
						return id;
					});
				},
				validation: (int id) => id == 42);
		});

		var exitCode = sut.Run(["contact", "42", "show"]);

		exitCode.Should().Be(0);
		handlerCalled.Should().BeTrue();
	}

	[TestMethod]
	[Description("Regression guard: verifies command is incomplete and no interactive is set so that scoped help is rendered.")]
	public void When_CommandIsIncompleteAndNoInteractiveIsSet_Then_ScopedHelpIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Context("contact", contact =>
		{
			contact.Context("{id:int}", scope =>
			{
				scope.Map("show", (int id) => id)
					.WithDescription("Show one contact");
			});
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "--no-interactive"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("show");
	}

	[TestMethod]
	[Description("Regression guard: verifies command is incomplete and interactive is allowed so that deeplink returns success.")]
	public void When_CommandIsIncompleteAndInteractiveIsAllowed_Then_DeeplinkReturnsSuccess()
	{
		var sut = ReplApp.Create();
		sut.Context("contact", contact =>
		{
			contact.Context("{id:int}", scope =>
			{
				scope.Map("show", (int id) => id);
			});
		});

		var output = ConsoleCaptureHelper.CaptureWithInput("exit\n", () => sut.Run(["contact", "42"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("[contact/42]>");
	}

	[TestMethod]
	[Description("Regression guard: verifies context validation fails so that framework validation message is rendered.")]
	public void When_ContextValidationFails_Then_FrameworkValidationMessageIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Context("contact", contact =>
		{
			contact.Context("{id:int}",
				scope => scope.Map("show", (int id) => id),
				validation: (int id) => id == 42);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "99", "show"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation: Scope validation failed for 'contact {id:int}'.");
		output.Text.Should().Contain("id: 99");
	}

	[TestMethod]
	[Description("Regression guard: verifies context validation returns string so that message is rendered as validation failure.")]
	public void When_ContextValidationReturnsString_Then_MessageIsRenderedAsValidationFailure()
	{
		var sut = ReplApp.Create();
		sut.Context("contact", contact =>
		{
			contact.Context("{id:int}",
				scope => scope.Map("show", (int id) => id),
				validation: (int id) => id == 42 ? string.Empty : "Contact not found.");
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "99", "show"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation: Contact not found.");
	}

	[TestMethod]
	[Description("Regression guard: verifies scoped literal command beats dynamic context validation so that list can execute from parent scope.")]
	public void When_InteractiveScopedLiteralCommandAndDynamicContextExist_Then_LiteralCommandExecutes()
	{
		var sut = ReplApp.Create()
			.UseDefaultInteractive();
		sut.Context("contact", contact =>
		{
			contact.Map("list", () => "ok");
			contact.Context("{name}",
				scope => scope.Map("show", (string name) => name),
				validation: (string name) => string.Equals(name, "Carl de Billy", StringComparison.OrdinalIgnoreCase));
		});

		var output = ConsoleCaptureHelper.CaptureWithInput("contact\n\"Carl de Billy\" show\nlist\nexit\n", () => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ok");
		output.Text.Should().NotContain("Scope validation failed");
	}
}


