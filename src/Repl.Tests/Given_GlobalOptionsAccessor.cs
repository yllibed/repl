using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_GlobalOptionsAccessor
{
	[TestMethod]
	[Description("GetValue returns parsed string value after Update.")]
	public void When_StringOptionParsed_Then_GetValueReturnsIt()
	{
		var sut = CreateAccessor("tenant");
		sut.Update(Values(("tenant", "acme")));

		sut.GetValue<string>("tenant").Should().Be("acme");
	}

	[TestMethod]
	[Description("GetValue returns typed int value after Update.")]
	public void When_IntOptionParsed_Then_GetValueReturnsTypedValue()
	{
		var sut = CreateAccessor<int>("port");
		sut.Update(Values(("port", "8080")));

		sut.GetValue<int>("port").Should().Be(8080);
	}

	[TestMethod]
	[Description("GetValue returns typed bool value after Update.")]
	public void When_BoolOptionParsed_Then_GetValueReturnsTypedValue()
	{
		var sut = CreateAccessor<bool>("verbose");
		sut.Update(Values(("verbose", "true")));

		sut.GetValue<bool>("verbose").Should().BeTrue();
	}

	[TestMethod]
	[Description("GetValue returns default when option not provided.")]
	public void When_OptionNotProvided_Then_GetValueReturnsDefault()
	{
		var sut = CreateAccessor("tenant");
		sut.Update(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

		sut.GetValue<string>("tenant").Should().BeNull();
	}

	[TestMethod]
	[Description("GetValue returns caller default when option not provided.")]
	public void When_OptionNotProvided_Then_GetValueReturnsCallerDefault()
	{
		var sut = CreateAccessor("tenant");
		sut.Update(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

		sut.GetValue<string>("tenant", "fallback").Should().Be("fallback");
	}

	[TestMethod]
	[Description("GetValue returns registration default when option not provided.")]
	public void When_OptionNotProvidedButRegisteredDefault_Then_GetValueReturnsRegisteredDefault()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption<int>("port", defaultValue: 3000);
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

		sut.GetValue<int>("port").Should().Be(3000);
	}

	[TestMethod]
	[Description("HasValue returns false before parsing.")]
	public void When_NeverUpdated_Then_HasValueReturnsFalse()
	{
		var sut = CreateAccessor("tenant");

		sut.HasValue("tenant").Should().BeFalse();
	}

	[TestMethod]
	[Description("HasValue returns true after parsing.")]
	public void When_OptionParsed_Then_HasValueReturnsTrue()
	{
		var sut = CreateAccessor("tenant");
		sut.Update(Values(("tenant", "acme")));

		sut.HasValue("tenant").Should().BeTrue();
	}

	[TestMethod]
	[Description("GetRawValues returns all values for repeated option.")]
	public void When_OptionRepeated_Then_GetRawValuesReturnsAll()
	{
		var sut = CreateAccessor("tag");
		sut.Update(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
		{
			["tag"] = (IReadOnlyList<string>)["alpha", "beta"],
		});

		sut.GetRawValues("tag").Should().Equal("alpha", "beta");
	}

	[TestMethod]
	[Description("GetRawValues returns empty for unset option.")]
	public void When_OptionNotSet_Then_GetRawValuesReturnsEmpty()
	{
		var sut = CreateAccessor("tag");
		sut.Update(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

		sut.GetRawValues("tag").Should().BeEmpty();
	}

	[TestMethod]
	[Description("GetOptionNames returns all parsed option names.")]
	public void When_MultipleOptionsParsed_Then_GetOptionNamesReturnsThem()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption<string>("tenant");
		parsing.AddGlobalOption<int>("port");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("tenant", "acme"), ("port", "8080")));

		sut.GetOptionNames().Should().Contain("tenant").And.Contain("port");
	}

	[TestMethod]
	[Description("Update replaces previous values (per-invocation semantics).")]
	public void When_UpdatedTwice_Then_SecondValuesReplacePrevious()
	{
		var sut = CreateAccessor("tenant");
		sut.Update(Values(("tenant", "first")));
		sut.Update(Values(("tenant", "second")));

		sut.GetValue<string>("tenant").Should().Be("second");
	}

	[TestMethod]
	[Description("AddGlobalOption with string type name works.")]
	public void When_RegisteredWithStringTypeName_Then_GetValueConvertsCorrectly()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("port", "int");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("port", "9090")));

		sut.GetValue<int>("port").Should().Be(9090);
	}

	private static GlobalOptionsSnapshot CreateAccessor(string name)
		=> CreateAccessor<string>(name);

	private static GlobalOptionsSnapshot CreateAccessor<T>(string name)
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption<T>(name);
		return new GlobalOptionsSnapshot(parsing);
	}

	private static Dictionary<string, IReadOnlyList<string>> Values(
		params (string Key, string Value)[] entries)
	{
		var dict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key, value) in entries)
		{
			dict[key] = [value];
		}

		return dict;
	}
}
