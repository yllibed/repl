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
}
