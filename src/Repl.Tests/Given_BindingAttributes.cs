using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_BindingAttributes
{
	[TestMethod]
	[Description("Regression guard: verifies creating FromServices attribute with key so that keyed DI directive can be configured.")]
	public void When_CreatingFromServicesAttributeWithKey_Then_KeyIsStored()
	{
		var attribute = new FromServicesAttribute("alpha");

		attribute.Key.Should().Be("alpha");
	}

	[TestMethod]
	[Description("Regression guard: verifies creating FromServices attribute with empty key so that invalid keyed DI directive is rejected.")]
	public void When_CreatingFromServicesAttributeWithEmptyKey_Then_ExceptionIsThrown()
	{
		var action = () => _ = new FromServicesAttribute(string.Empty);

		action.Should().Throw<ArgumentException>()
			.WithMessage("*Service key cannot be empty*");
	}
}
