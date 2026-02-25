using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_RouteResolution
{
	[TestMethod]
	[Description("Regression guard: verifies mapping unknown constraint so that invalid operation is thrown.")]
	public void When_MappingUnknownConstraint_Then_InvalidOperationIsThrown()
	{
		var sut = ReplApp.Create();

		var action = () => sut.Map("contact {id:unknown}", (string id) => id);

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*Unknown route constraint*");
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving literal and dynamic so that literal route wins.")]
	public void When_ResolvingLiteralAndDynamic_Then_LiteralRouteWins()
	{
		var sut = ReplApp.Create();
		var literal = sut.Map("contact list", () => "literal");
		sut.Map("contact {name}", (string name) => name);

		var match = sut.Resolve(["contact", "list"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(literal);
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving int and string so that int route wins for numeric token.")]
	public void When_ResolvingIntAndString_Then_IntRouteWinsForNumericToken()
	{
		var sut = ReplApp.Create();
		sut.Map("contact {name}", (string name) => name);
		var intRoute = sut.Map("contact {id:int}", (int id) => id);

		var match = sut.Resolve(["contact", "42"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(intRoute);
		match.Values.Should().ContainKey("id");
		match.Values["id"].Should().Be("42");
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving int and string so that int route wins for hexadecimal token.")]
	public void When_ResolvingIntAndString_Then_IntRouteWinsForHexadecimalToken()
	{
		var sut = ReplApp.Create();
		sut.Map("contact {name}", (string name) => name);
		var intRoute = sut.Map("contact {id:int}", (int id) => id);

		var match = sut.Resolve(["contact", "0xFF"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(intRoute);
		match.Values.Should().ContainKey("id");
		match.Values["id"].Should().Be("0xFF");
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving int and string so that string route wins for text token.")]
	public void When_ResolvingIntAndString_Then_StringRouteWinsForTextToken()
	{
		var sut = ReplApp.Create();
		var stringRoute = sut.Map("contact {name}", (string name) => name);
		sut.Map("contact {id:int}", (int id) => id);

		var match = sut.Resolve(["contact", "alice"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(stringRoute);
		match.Values.Should().ContainKey("name");
		match.Values["name"].Should().Be("alice");
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving url and uri so that url route wins for http/https input.")]
	public void When_ResolvingUrlAndUri_Then_UrlRouteWinsForHttpInput()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value:uri}", (string value) => value);
		var urlRoute = sut.Map("target {value:url}", (string value) => value);

		var match = sut.Resolve(["target", "https://example.com"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(urlRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving url and uri so that uri route wins for non-http absolute URI input.")]
	public void When_ResolvingUrlAndUri_Then_UriRouteWinsForNonHttpUri()
	{
		var sut = ReplApp.Create();
		var uriRoute = sut.Map("target {value:uri}", (string value) => value);
		sut.Map("target {value:url}", (string value) => value);

		var match = sut.Resolve(["target", "mailto:alice@example.com"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(uriRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving urn and uri so that urn route wins for urn input.")]
	public void When_ResolvingUrnAndUri_Then_UrnRouteWinsForUrnInput()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value:uri}", (string value) => value);
		var urnRoute = sut.Map("target {value:urn}", (string value) => value);

		var match = sut.Resolve(["target", "urn:isbn:0451450523"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(urnRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving email and string so that email route wins for practical email inputs.")]
	public void When_ResolvingEmailAndString_Then_EmailRouteWinsForEmailInput()
	{
		var sut = ReplApp.Create();
		sut.Map("target {value}", (string value) => value);
		var emailRoute = sut.Map("target {value:email}", (string value) => value);

		var match = sut.Resolve(["target", "alice@example.com"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(emailRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving date and string so that date route wins for ISO date input.")]
	public void When_ResolvingDateAndString_Then_DateRouteWinsForIsoDateInput()
	{
		var sut = ReplApp.Create();
		sut.Map("report {value}", (string value) => value);
		var dateRoute = sut.Map("report {value:date}", (string value) => value);

		var match = sut.Resolve(["report", "2026-02-19"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(dateRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving datetimeoffset and datetime so that offset route wins when timezone is present.")]
	public void When_ResolvingDateTimeOffsetAndDateTime_Then_DateTimeOffsetRouteWinsForOffsetInput()
	{
		var sut = ReplApp.Create();
		sut.Map("report {value:datetime}", (string value) => value);
		var offsetRoute = sut.Map("report {value:datetimeoffset}", (string value) => value);

		var match = sut.Resolve(["report", "2026-02-19T14:30:00Z"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(offsetRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies resolving timespan and string so that timespan route wins for compact literal input.")]
	public void When_ResolvingTimeSpanAndString_Then_TimeSpanRouteWinsForCompactLiteral()
	{
		var sut = ReplApp.Create();
		sut.Map("delay {value}", (string value) => value);
		var timespanRoute = sut.Map("delay {value:timespan}", (string value) => value);

		var match = sut.Resolve(["delay", "1h30"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(timespanRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies temporal aliases are parsed so that date-time alias behaves like datetime constraint.")]
	public void When_ResolvingDateTimeAliasAndString_Then_DateTimeAliasRouteWins()
	{
		var sut = ReplApp.Create();
		sut.Map("report {value}", (string value) => value);
		var aliasRoute = sut.Map("report {value:date-time}", (string value) => value);

		var match = sut.Resolve(["report", "2026-02-19T14:30"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(aliasRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies date-time-offset alias is parsed so that offset-aware input resolves to alias route.")]
	public void When_ResolvingDateTimeOffsetAliasAndString_Then_AliasRouteWins()
	{
		var sut = ReplApp.Create();
		sut.Map("report {value}", (string value) => value);
		var aliasRoute = sut.Map("report {value:date-time-offset}", (string value) => value);

		var match = sut.Resolve(["report", "2026-02-19T14:30:00+02:00"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(aliasRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies time-only alias is parsed so that HH:mm input resolves to alias route.")]
	public void When_ResolvingTimeOnlyAliasAndString_Then_AliasRouteWins()
	{
		var sut = ReplApp.Create();
		sut.Map("report {value}", (string value) => value);
		var aliasRoute = sut.Map("report {value:time-only}", (string value) => value);

		var match = sut.Resolve(["report", "09:45"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(aliasRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies custom route constraint is registered so that registered custom token can be parsed and matched.")]
	public void When_ResolvingCustomConstraint_Then_CustomRouteCanMatch()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.AddRouteConstraint("slug", value =>
				value.All(ch => char.IsLower(ch) || char.IsDigit(ch) || ch == '-')));
		sut.Map("article {id}", (string id) => id);
		var customRoute = sut.Map("article {id:slug}", (string id) => id);

		var match = sut.Resolve(["article", "hello-world-1"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(customRoute);
	}

	[TestMethod]
	[Description("Regression guard: verifies custom route constraints overlap by name so that duplicate custom constraints are rejected as ambiguous.")]
	public void When_MappingDuplicateCustomConstraintAtSameLevel_Then_MappingFailsAsAmbiguous()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.AddRouteConstraint("slug", value =>
				value.All(ch => char.IsLower(ch) || char.IsDigit(ch) || ch == '-')));
		sut.Map("article {id:slug}", (string id) => id);

		var action = () => sut.Map("article {other:slug}", (string other) => other);

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*Ambiguous route template*");
	}

	[TestMethod]
	[Description("Regression guard: verifies routes are ambiguous by overlapping constraints so that mapping fails.")]
	public void When_RoutesAreAmbiguousByOverlappingConstraints_Then_MappingFails()
	{
		var sut = ReplApp.Create();
		sut.Map("contact {id:int}", (int id) => id);

		var action = () => sut.Map("contact {other:int}", (int other) => other);

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*Ambiguous route template*");
	}

	[TestMethod]
	[Description("Regression guard: verifies routes do not match input so that resolve returns null.")]
	public void When_RoutesDoNotMatchInput_Then_ResolveReturnsNull()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok");

		var match = sut.Resolve(["property", "list"]);

		match.Should().BeNull();
	}

	[TestMethod]
	[Description("Verifies optional parameter is omitted so that route still matches.")]
	public void When_OptionalParameterIsOmitted_Then_RouteStillMatches()
	{
		var sut = ReplApp.Create();
		var route = sut.Map("contact add {name?}", (string? name) => name ?? "none");

		var match = sut.Resolve(["contact", "add"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(route);
		match.Values.Should().NotContainKey("name");
	}

	[TestMethod]
	[Description("Verifies optional parameter is provided so that value is bound.")]
	public void When_OptionalParameterIsProvided_Then_ValueIsBound()
	{
		var sut = ReplApp.Create();
		var route = sut.Map("contact add {name?}", (string? name) => name ?? "none");

		var match = sut.Resolve(["contact", "add", "Alice"]);

		match.Should().NotBeNull();
		match!.Route.Command.Should().BeSameAs(route);
		match.Values.Should().ContainKey("name");
		match.Values["name"].Should().Be("Alice");
	}

	[TestMethod]
	[Description("Verifies required segment following optional so that parsing throws.")]
	public void When_RequiredFollowsOptional_Then_ParsingThrows()
	{
		var sut = ReplApp.Create();

		var action = () => sut.Map("contact {name?} {id:int}", (string? name, int id) => id);

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*required segment cannot follow an optional segment*");
	}

	[TestMethod]
	[Description("Verifies optional and shorter route overlap so that ambiguity is detected.")]
	public void When_OptionalAndShorterRouteOverlap_Then_AmbiguityIsDetected()
	{
		var sut = ReplApp.Create();
		sut.Map("contact add", () => "no-arg");

		var action = () => sut.Map("contact add {name?}", (string? name) => name ?? "none");

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*Ambiguous route template*");
	}
}






