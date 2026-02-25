using Microsoft.Extensions.DependencyInjection;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ContextHierarchyBinding
{
	[TestMethod]
	[Description("Regression guard: verifies parameter can bind from context hierarchy so that dynamic scope values are injected by type.")]
	public void When_UsingFromContextAttribute_Then_ContextValueIsInjected()
	{
		var sut = ReplApp.Create();
		sut.Context("contact {id:int}", map =>
		{
			map.Map("show", ([FromContext] int contextId) => contextId);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("42");
	}

	[TestMethod]
	[Description("Regression guard: verifies [FromContext] overrides route binding so that explicit source direction is honored.")]
	public void When_UsingFromContextWithConflictingRouteParameterName_Then_ContextValueWins()
	{
		var sut = ReplApp.Create();
		sut.Context("contact {id:int}", map =>
		{
			map.Map("show {id}", ([FromContext] int id) => id);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "show", "99", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("42");
	}

	[TestMethod]
	[Description("Regression guard: verifies parameter can bind from services explicitly so that DI value is selected over context value.")]
	public void When_UsingFromServicesAttribute_Then_ServiceValueIsInjected()
	{
		var sut = ReplApp.Create(services => services.AddSingleton<IMessageSource>(new MessageSource("svc-value")));
		sut.Context("contact {id:int}", map =>
		{
			map.Map("show", ([FromServices] IMessageSource source) => source.Value);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("svc-value");
	}

	[TestMethod]
	[Description("Regression guard: verifies [FromServices] overrides route binding so that explicit source direction is honored.")]
	public void When_UsingFromServicesWithConflictingRouteParameterName_Then_ServiceValueWins()
	{
		var sut = ReplApp.Create(services => services.AddSingleton("svc-name"));
		sut.Map("show {value}", ([FromServices] string value) => value);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["show", "route-name", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("svc-name");
	}

	[TestMethod]
	[Description("Regression guard: verifies parameter can bind keyed service explicitly so that multiple registrations of the same type remain addressable.")]
	public void When_UsingFromServicesWithKey_Then_KeyedServiceValueIsInjected()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddKeyedSingleton<IMessageSource>("alpha", new MessageSource("svc-alpha"));
			services.AddKeyedSingleton<IMessageSource>("beta", new MessageSource("svc-beta"));
		});
		sut.Context("contact {id:int}", map =>
		{
			map.Map("show", ([FromServices("beta")] IMessageSource source) => source.Value);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("svc-beta");
	}

	[TestMethod]
	[Description("Regression guard: verifies missing keyed service resolution fails so that configuration mistakes produce a structured validation error.")]
	public void When_UsingFromServicesWithMissingKey_Then_ValidationErrorIsReturned()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddKeyedSingleton<IMessageSource>("alpha", new MessageSource("svc-alpha"));
		});
		sut.Context("contact {id:int}", map =>
		{
			map.Map("show", ([FromServices("beta")] IMessageSource source) => source.Value);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "show", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Unable to resolve parameter 'source' from services with key 'beta'.");
	}

	[TestMethod]
	[Description("Regression guard: verifies default [FromServices] does not resolve keyed registrations implicitly so that keyed lookups stay explicit.")]
	public void When_UsingFromServicesWithoutKeyAgainstOnlyKeyedRegistration_Then_ValidationErrorIsReturned()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddKeyedSingleton<IMessageSource>("alpha", new MessageSource("svc-alpha"));
		});
		sut.Context("contact {id:int}", map =>
		{
			map.Map("show", ([FromServices] IMessageSource source) => source.Value);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "show", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Unable to resolve parameter 'source' from services.");
	}

	[TestMethod]
	[Description("Regression guard: verifies ambiguous default binding between context and services so that command fails with structured validation output.")]
	public void When_BindingIsAmbiguousBetweenContextAndServices_Then_ValidationErrorIsReturned()
	{
		var sut = ReplApp.Create(services => services.AddSingleton<string>("service-name"));
		sut.Context("contact {name}", map =>
		{
			map.Map("show", (string value) => value);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "alice", "show", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Validation: Ambiguous binding for parameter 'value'.");
	}

	[TestMethod]
	[Description("Regression guard: verifies collection parameter binds from all matching ancestors so that nearest-first values are returned.")]
	public void When_BindingCollectionFromContextHierarchy_Then_AllMatchingAncestorsAreInjected()
	{
		var sut = ReplApp.Create();
		sut.Context("client {clientId:int}", client =>
		{
			client.Context("contact {contactId:int}", contact =>
			{
				contact.Map("show", ([FromContext] List<int> ids) => string.Join(',', ids));
			});
		});

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["client", "7", "contact", "42", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("42,7");
	}

	[TestMethod]
	[Description("Regression guard: verifies [FromContext(All = true)] binds all matching ancestors so that explicit all-context binding is supported.")]
	public void When_UsingFromContextAllAttribute_Then_AllMatchingAncestorsAreInjected()
	{
		var sut = ReplApp.Create();
		sut.Context("client {clientId:int}", client =>
		{
			client.Context("contact {contactId:int}", contact =>
			{
				contact.Map("show", ([FromContext(All = true)] List<int> ids) => string.Join(',', ids));
			});
		});

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["client", "7", "contact", "42", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("42,7");
	}

	[TestMethod]
	[Description("Regression guard: verifies [FromContext(All = true)] overrides route binding so that explicit all-context source ignores input binding.")]
	public void When_UsingFromContextAllWithConflictingRouteParameterName_Then_ContextCollectionWins()
	{
		var sut = ReplApp.Create();
		sut.Context("client {id:int}", client =>
		{
			client.Map("show {id}", ([FromContext(All = true)] List<int> ids) => string.Join(',', ids));
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["client", "7", "show", "99", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("7");
	}

	[TestMethod]
	[Description("Regression guard: verifies [FromContext(All = true)] rejects scalar target so that misconfiguration fails with a structured validation error.")]
	public void When_UsingFromContextAllAttributeOnScalar_Then_ValidationErrorIsReturned()
	{
		var sut = ReplApp.Create();
		sut.Context("contact {id:int}", map =>
		{
			map.Map("show", ([FromContext(All = true)] int id) => id);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "show", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("[FromContext(All = true)] requires a collection parameter type.");
	}

	[TestMethod]
	[Description("Regression guard: verifies [FromContext(All = true)] returns empty collection when no match exists so that handlers can decide behavior explicitly.")]
	public void When_UsingFromContextAllWithoutMatchingAncestors_Then_EmptyCollectionIsInjected()
	{
		var sut = ReplApp.Create();
		sut.Context("contact {id:int}", map =>
		{
			map.Map("show", ([FromContext(All = true)] List<Guid> ids) => ids.Count);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("0");
	}

	[TestMethod]
	[Description("Regression guard: verifies urn-scoped context can inject Uri from context hierarchy so that typed Uri is available to handlers.")]
	public void When_BindingUrnScopedContextAsUri_Then_UriValueIsInjectedFromContext()
	{
		var sut = ReplApp.Create();
		sut.Context("resource {id:urn}", resource =>
		{
			resource.Map("show", ([FromContext] Uri id) => id.Scheme);
		});

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["resource", "urn:isbn:0451450523", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("urn");
	}

	[TestMethod]
	[Description("Regression guard: verifies date-scoped context can inject DateOnly from context hierarchy so that typed date is available to handlers.")]
	public void When_BindingDateScopedContextAsDateOnly_Then_DateOnlyValueIsInjectedFromContext()
	{
		var sut = ReplApp.Create();
		sut.Context("day {value:date}", scope =>
		{
			scope.Map("show", ([FromContext] DateOnly value) => value.Day);
		});

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["day", "2026-02-19", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("19");
	}

	[TestMethod]
	[Description("Regression guard: verifies timespan-scoped context can inject TimeSpan from context hierarchy so that typed duration is available to handlers.")]
	public void When_BindingTimeSpanScopedContextAsTimeSpan_Then_TimeSpanValueIsInjectedFromContext()
	{
		var sut = ReplApp.Create();
		sut.Context("delay {value:timespan}", scope =>
		{
			scope.Map("show", ([FromContext] TimeSpan value) => (int)value.TotalMinutes);
		});

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["delay", "1h30", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("90");
	}

	private interface IMessageSource
	{
		string Value { get; }
	}

	private sealed class MessageSource(string value) : IMessageSource
	{
		public string Value { get; } = value;
	}
}
