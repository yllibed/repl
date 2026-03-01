using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_GlobalOptionParser
{
	[TestMethod]
	[Description("Regression guard: verifies registered custom global options are consumed from argv so they do not leak into command token parsing.")]
	public void When_CustomGlobalOptionIsRegistered_Then_ParserConsumesItIntoCustomGlobalValues()
	{
		var parsingOptions = new ParsingOptions();
		parsingOptions.AddGlobalOption<string>("tenant");

		var parsed = GlobalOptionParser.Parse(
			["users", "list", "--tenant", "acme"],
			new OutputOptions(),
			parsingOptions);

		parsed.RemainingTokens.Should().Equal("users", "list");
		parsed.CustomGlobalNamedOptions.Should().ContainKey("tenant");
		parsed.CustomGlobalNamedOptions["tenant"].Should().ContainSingle().Which.Should().Be("acme");
	}

	[TestMethod]
	[Description("Regression guard: verifies custom global aliases are resolved so short and alternate tokens map to the canonical global option name.")]
	public void When_CustomGlobalAliasIsUsed_Then_ValueIsCapturedUnderCanonicalName()
	{
		var parsingOptions = new ParsingOptions();
		parsingOptions.AddGlobalOption<string>("tenant", aliases: ["-t", "--org"]);

		var parsed = GlobalOptionParser.Parse(
			["users", "list", "-t:acme"],
			new OutputOptions(),
			parsingOptions);

		parsed.RemainingTokens.Should().Equal("users", "list");
		parsed.CustomGlobalNamedOptions.Should().ContainKey("tenant");
		parsed.CustomGlobalNamedOptions["tenant"].Should().ContainSingle().Which.Should().Be("acme");
	}

	[TestMethod]
	[Description("Regression guard: verifies built-in global flags honor case-insensitive mode so --HELP and similar casing variants remain consistent with custom option behavior.")]
	public void When_BuiltInGlobalFlagUsesDifferentCaseInInsensitiveMode_Then_FlagIsRecognized()
	{
		var parsingOptions = new ParsingOptions
		{
			OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive,
		};

		var parsed = GlobalOptionParser.Parse(
			["--HELP"],
			new OutputOptions(),
			parsingOptions);

		parsed.HelpRequested.Should().BeTrue();
		parsed.RemainingTokens.Should().BeEmpty();
	}

	[TestMethod]
	[Description("Regression guard: verifies custom global options can consume signed numeric values so '--tenant -1' is treated as option value instead of leaking to remaining tokens.")]
	public void When_CustomGlobalOptionValueIsSignedNumber_Then_ValueIsConsumed()
	{
		var parsingOptions = new ParsingOptions();
		parsingOptions.AddGlobalOption<string>("tenant");

		var parsed = GlobalOptionParser.Parse(
			["users", "--tenant", "-1"],
			new OutputOptions(),
			parsingOptions);

		parsed.RemainingTokens.Should().Equal("users");
		parsed.CustomGlobalNamedOptions.Should().ContainKey("tenant");
		parsed.CustomGlobalNamedOptions["tenant"].Should().ContainSingle().Which.Should().Be("-1");
	}
}
