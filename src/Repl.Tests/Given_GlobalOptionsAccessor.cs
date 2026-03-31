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
	[Description("Session baseline values persist when interactive command provides no override.")]
	public void When_BaselineSet_Then_UpdateWithEmptyPreservesBaseline()
	{
		var sut = CreateAccessor("env");
		sut.Update(Values(("env", "staging")));
		sut.SetSessionBaseline();
		sut.Update(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

		sut.GetValue<string>("env").Should().Be("staging");
	}

	[TestMethod]
	[Description("HasValue returns false for baseline-only keys not explicitly provided.")]
	public void When_BaselineKeyNotExplicitlyProvided_Then_HasValueReturnsFalse()
	{
		var sut = CreateAccessor("env");
		sut.Update(Values(("env", "staging")));
		sut.SetSessionBaseline();
		sut.Update(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

		sut.HasValue("env").Should().BeFalse();
		sut.GetValue<string>("env").Should().Be("staging"); // still accessible via GetValue
	}

	[TestMethod]
	[Description("HasValue returns true when option is explicitly provided even with baseline.")]
	public void When_BaselineKeyExplicitlyOverridden_Then_HasValueReturnsTrue()
	{
		var sut = CreateAccessor("env");
		sut.Update(Values(("env", "staging")));
		sut.SetSessionBaseline();
		sut.Update(Values(("env", "prod")));

		sut.HasValue("env").Should().BeTrue();
	}

	[TestMethod]
	[Description("Per-command override takes precedence over session baseline.")]
	public void When_BaselineSetAndOverridden_Then_OverrideTakesPrecedence()
	{
		var sut = CreateAccessor("env");
		sut.Update(Values(("env", "staging")));
		sut.SetSessionBaseline();
		sut.Update(Values(("env", "prod")));

		sut.GetValue<string>("env").Should().Be("prod");
	}

	[TestMethod]
	[Description("Session baseline is restored after override is gone.")]
	public void When_OverrideRemovedOnNextUpdate_Then_BaselineRestored()
	{
		var sut = CreateAccessor("env");
		sut.Update(Values(("env", "staging")));
		sut.SetSessionBaseline();

		// Command with override
		sut.Update(Values(("env", "prod")));
		sut.GetValue<string>("env").Should().Be("prod");

		// Next command without override — baseline restored
		sut.Update(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
		sut.GetValue<string>("env").Should().Be("staging");
	}

	[TestMethod]
	[Description("Override does not mutate the session baseline.")]
	public void When_OverrideApplied_Then_BaselineRemainsUnchanged()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption<string>("env");
		parsing.AddGlobalOption<int>("port");
		var sut = new GlobalOptionsSnapshot(parsing);

		sut.Update(Values(("env", "staging"), ("port", "3000")));
		sut.SetSessionBaseline();

		// Override only env
		sut.Update(Values(("env", "prod")));
		sut.GetValue<string>("env").Should().Be("prod");
		sut.GetValue<int>("port").Should().Be(3000); // baseline preserved

		// Next command — both baseline values restored
		sut.Update(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
		sut.GetValue<string>("env").Should().Be("staging");
		sut.GetValue<int>("port").Should().Be(3000);
	}

	[TestMethod]
	[Description("AddGlobalOption with string type name 'int' works.")]
	public void When_RegisteredWithStringTypeName_Int_Then_GetValueConvertsCorrectly()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("port", "int");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("port", "9090")));

		sut.GetValue<int>("port").Should().Be(9090);
	}

	[TestMethod]
	[Description("AddGlobalOption with string type name 'bool' works.")]
	public void When_RegisteredWithStringTypeName_Bool_Then_GetValueConvertsCorrectly()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("verbose", "bool");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("verbose", "true")));

		sut.GetValue<bool>("verbose").Should().BeTrue();
	}

	[TestMethod]
	[Description("AddGlobalOption with string type name 'long' works.")]
	public void When_RegisteredWithStringTypeName_Long_Then_GetValueConvertsCorrectly()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("size", "long");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("size", "9999999999")));

		sut.GetValue<long>("size").Should().Be(9_999_999_999L);
	}

	[TestMethod]
	[Description("AddGlobalOption with string type name 'guid' works.")]
	public void When_RegisteredWithStringTypeName_Guid_Then_GetValueConvertsCorrectly()
	{
		var id = Guid.NewGuid();
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("id", "guid");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("id", id.ToString())));

		sut.GetValue<Guid>("id").Should().Be(id);
	}

	[TestMethod]
	[Description("AddGlobalOption with string type name 'uri' works.")]
	public void When_RegisteredWithStringTypeName_Uri_Then_GetValueConvertsCorrectly()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("endpoint", "uri");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("endpoint", "https://example.com")));

		sut.GetValue<Uri>("endpoint").Should().Be(new Uri("https://example.com"));
	}

	[TestMethod]
	[Description("AddGlobalOption with string type name 'date' works.")]
	public void When_RegisteredWithStringTypeName_Date_Then_GetValueConvertsCorrectly()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("since", "date");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("since", "2026-03-30")));

		sut.GetValue<DateOnly>("since").Should().Be(new DateOnly(2026, 3, 30));
	}

	[TestMethod]
	[Description("AddGlobalOption with string type name 'timespan' works.")]
	public void When_RegisteredWithStringTypeName_TimeSpan_Then_GetValueConvertsCorrectly()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("timeout", "timespan");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("timeout", "00:05:00")));

		sut.GetValue<TimeSpan>("timeout").Should().Be(TimeSpan.FromMinutes(5));
	}

	[TestMethod]
	[Description("AddGlobalOption with string type name 'datetime' works.")]
	public void When_RegisteredWithStringTypeName_DateTime_Then_GetValueConvertsCorrectly()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("at", "datetime");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("at", "2026-03-30T14:00:00")));

		sut.GetValue<DateTime>("at").Day.Should().Be(30);
	}

	[TestMethod]
	[Description("AddGlobalOption with string type name 'string' works.")]
	public void When_RegisteredWithStringTypeName_String_Then_GetValueReturnsIt()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("name", "string");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("name", "hello")));

		sut.GetValue<string>("name").Should().Be("hello");
	}

	[TestMethod]
	[Description("AddGlobalOption with unknown type name throws ArgumentException.")]
	public void When_RegisteredWithUnknownTypeName_Then_Throws()
	{
		var parsing = new ParsingOptions();

		var act = () => parsing.AddGlobalOption("x", "foobar");

		act.Should().Throw<ArgumentException>()
			.Which.Message.Should().Contain("foobar");
	}

	[TestMethod]
	[Description("AddGlobalOption with case-variant type name works.")]
	public void When_RegisteredWithUpperCaseTypeName_Then_ResolvesCorrectly()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("port", "INT");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("port", "443")));

		sut.GetValue<int>("port").Should().Be(443);
	}

	[TestMethod]
	[Description("AddGlobalOption preserves ValueType from generic overload.")]
	public void When_RegisteredWithGenericOverload_Then_ValueTypeIsPreserved()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption<Guid>("id");

		parsing.GlobalOptions["id"].ValueType.Should().Be(typeof(Guid));
	}

	[TestMethod]
	[Description("AddGlobalOption preserves ValueType from string type name overload.")]
	public void When_RegisteredWithStringTypeNameOverload_Then_ValueTypeIsPreserved()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption("port", "int");

		parsing.GlobalOptions["port"].ValueType.Should().Be(typeof(int));
	}

	[TestMethod]
	[Description("AddGlobalOption with custom route constraint type name resolves as string.")]
	public void When_RegisteredWithCustomConstraintTypeName_Then_ResolvesAsString()
	{
		var parsing = new ParsingOptions();
		parsing.AddRouteConstraint("hex", value => value.All(c => "0123456789abcdefABCDEF".Contains(c, StringComparison.Ordinal)));
		parsing.AddGlobalOption("color", "hex");
		var sut = new GlobalOptionsSnapshot(parsing);
		sut.Update(Values(("color", "ff00aa")));

		sut.GetValue<string>("color").Should().Be("ff00aa");
		parsing.GlobalOptions["color"].ValueType.Should().Be(typeof(string));
	}

	[TestMethod]
	[Description("AddGlobalOption with custom constraint that doesn't exist still throws.")]
	public void When_RegisteredWithUnregisteredConstraintName_Then_Throws()
	{
		var parsing = new ParsingOptions();

		var act = () => parsing.AddGlobalOption("x", "nonexistent");

		act.Should().Throw<ArgumentException>()
			.Which.Message.Should().Contain("nonexistent");
	}

	[TestMethod]
	[Description("Duplicate global option name throws.")]
	public void When_RegisteringDuplicateName_Then_Throws()
	{
		var parsing = new ParsingOptions();
		parsing.AddGlobalOption<string>("tenant");

		var act = () => parsing.AddGlobalOption("tenant", "int");

		act.Should().Throw<InvalidOperationException>()
			.Which.Message.Should().Contain("tenant");
	}

	[TestMethod]
	[Description("GetValue with enum type converts string to enum.")]
	public void When_EnumOptionParsed_Then_GetValueReturnsTypedEnum()
	{
		var sut = CreateAccessor<LogLevel>("level");
		sut.Update(Values(("level", "Warning")));

		sut.GetValue<LogLevel>("level").Should().Be(LogLevel.Warning);
	}

	private enum LogLevel
	{
		Info,
		Warning,
		Error,
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
