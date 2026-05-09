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

	[TestMethod]
	[Description("Bool-typed global option does not consume next positional token.")]
	public void When_BoolGlobalOptionFollowedByCommand_Then_CommandNotConsumed()
	{
		var parsingOptions = new ParsingOptions();
		parsingOptions.AddGlobalOption<bool>("verbose");

		var parsed = GlobalOptionParser.Parse(
			["--verbose", "deploy"],
			new OutputOptions(),
			parsingOptions);

		parsed.RemainingTokens.Should().Equal("deploy");
		parsed.CustomGlobalNamedOptions["verbose"].Should().ContainSingle().Which.Should().Be("true");
	}

	[TestMethod]
	[Description("Bool-typed global option with inline value still works.")]
	public void When_BoolGlobalOptionWithInlineValue_Then_ValueIsUsed()
	{
		var parsingOptions = new ParsingOptions();
		parsingOptions.AddGlobalOption<bool>("verbose");

		var parsed = GlobalOptionParser.Parse(
			["--verbose=false", "deploy"],
			new OutputOptions(),
			parsingOptions);

		parsed.RemainingTokens.Should().Equal("deploy");
		parsed.CustomGlobalNamedOptions["verbose"].Should().ContainSingle().Which.Should().Be("false");
	}

	[TestMethod]
	[Description("Result-flow global options are consumed before command parsing and stored separately from custom global options.")]
	public void When_ResultFlowOptionsArePresent_Then_ParserConsumesThemIntoResultFlow()
	{
		var parsed = GlobalOptionParser.Parse(
			["users", "list", "--result:page-size=25", "--result:cursor", "abc", "--result:all", "--result:pager=off"],
			new OutputOptions(),
			new ParsingOptions());

		parsed.RemainingTokens.Should().Equal("users", "list");
		parsed.CustomGlobalNamedOptions.Should().BeEmpty();
		parsed.ResultFlow.PageSize.Should().Be(25);
		parsed.ResultFlow.Cursor.Should().Be("abc");
		parsed.ResultFlow.AllRequested.Should().BeTrue();
		parsed.ResultFlow.PagerMode.Should().Be(ReplPagerMode.Off);
	}

	[TestMethod]
	[Description("Result-flow pager modes parse the current public mode names.")]
	public void When_ResultFlowPagerModeIsFullOrInline_Then_ParserStoresMode()
	{
		var full = GlobalOptionParser.Parse(
			["users", "list", "--result:pager=full"],
			new OutputOptions(),
			new ParsingOptions());
		var inline = GlobalOptionParser.Parse(
			["users", "list", "--result:pager=inline"],
			new OutputOptions(),
			new ParsingOptions());

		full.ResultFlow.PagerMode.Should().Be(ReplPagerMode.Full);
		inline.ResultFlow.PagerMode.Should().Be(ReplPagerMode.Inline);
	}

	[TestMethod]
	[Description("Result-flow page size is clamped during global option parsing before it reaches handlers or page sources.")]
	public void When_ResultFlowPageSizeExceedsMaximum_Then_ParserClampsIt()
	{
		var outputOptions = new OutputOptions();
		outputOptions.ResultFlow.DefaultPageSize = 50;
		outputOptions.ResultFlow.MaxPageSize = 50;

		var parsed = GlobalOptionParser.Parse(
			["users", "list", "--result:page-size=2147483647"],
			outputOptions,
			new ParsingOptions());

		parsed.ResultFlow.PageSize.Should().Be(50);
	}

	[TestMethod]
	[Description("Result-flow cursor must be explicit so a missing value is reported before command binding.")]
	public void When_ResultFlowCursorValueIsMissing_Then_DiagnosticErrorIsProduced()
	{
		var parsed = GlobalOptionParser.Parse(
			["users", "list", "--result:cursor"],
			new OutputOptions(),
			new ParsingOptions());

		parsed.Diagnostics.Should().ContainSingle(diagnostic =>
			diagnostic.Severity == ParseDiagnosticSeverity.Error
			&& diagnostic.Message.Contains("cursor", StringComparison.OrdinalIgnoreCase));
	}

	[TestMethod]
	[Description("Result-flow cursor rejects token-like values that could corrupt downstream CLI reconstruction.")]
	public void When_ResultFlowCursorStartsWithDash_Then_DiagnosticErrorIsProduced()
	{
		var parsed = GlobalOptionParser.Parse(
			["users", "list", "--result:cursor=--result:all"],
			new OutputOptions(),
			new ParsingOptions());

		parsed.Diagnostics.Should().ContainSingle(diagnostic =>
			diagnostic.Severity == ParseDiagnosticSeverity.Error
			&& diagnostic.Message.Contains("cursor", StringComparison.OrdinalIgnoreCase)
			&& diagnostic.Message.Contains("option", StringComparison.OrdinalIgnoreCase));
	}

	[TestMethod]
	[Description("Result-flow cursor rejects control characters so rendered continuation text cannot inject terminal escapes.")]
	public void When_ResultFlowCursorContainsControlCharacter_Then_DiagnosticErrorIsProduced()
	{
		var parsed = GlobalOptionParser.Parse(
			["users", "list", "--result:cursor=abc\u001b[2J"],
			new OutputOptions(),
			new ParsingOptions());

		parsed.Diagnostics.Should().ContainSingle(diagnostic =>
			diagnostic.Severity == ParseDiagnosticSeverity.Error
			&& diagnostic.Message.Contains("control", StringComparison.OrdinalIgnoreCase));
	}

	[TestMethod]
	[Description("Result-flow cursor rejects C1 control characters that can be interpreted by legacy terminals.")]
	public void When_ResultFlowCursorContainsC1Control_Then_DiagnosticErrorIsProduced()
	{
		var parsed = GlobalOptionParser.Parse(
			["users", "list", "--result:cursor=abc\u009b2J"],
			new OutputOptions(),
			new ParsingOptions());

		parsed.Diagnostics.Should().ContainSingle(diagnostic =>
			diagnostic.Severity == ParseDiagnosticSeverity.Error
			&& diagnostic.Message.Contains("control", StringComparison.OrdinalIgnoreCase));
	}
}
