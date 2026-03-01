using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_InvocationOptionParser
{
	[TestMethod]
	[Description("Regression guard: verifies strict unknown-option mode emits a parser error so invocation fails fast instead of silently accepting typos.")]
	public void When_UnknownOptionAndStrictMode_Then_DiagnosticErrorIsProduced()
	{
		var parsingOptions = new ParsingOptions
		{
			AllowUnknownOptions = false,
		};

		var parsed = InvocationOptionParser.Parse(
			["--outpu", "json"],
			parsingOptions,
			knownOptionNames: ["output"]);

		parsed.HasErrors.Should().BeTrue();
		parsed.Diagnostics.Should().ContainSingle();
		parsed.Diagnostics[0].Severity.Should().Be(ParseDiagnosticSeverity.Error);
		parsed.Diagnostics[0].Suggestion.Should().Be("--output");
		parsed.NamedOptions.Should().NotContainKey("outpu");
	}

	[TestMethod]
	[Description("Regression guard: verifies permissive unknown-option mode preserves legacy behavior so unknown named options still bind by parameter name.")]
	public void When_UnknownOptionAndPermissiveMode_Then_OptionIsStoredWithoutDiagnostic()
	{
		var parsingOptions = new ParsingOptions
		{
			AllowUnknownOptions = true,
		};

		var parsed = InvocationOptionParser.Parse(
			["--mystery", "value"],
			parsingOptions,
			knownOptionNames: ["output"]);

		parsed.HasErrors.Should().BeFalse();
		parsed.NamedOptions.Should().ContainKey("mystery");
		parsed.NamedOptions["mystery"].Should().ContainSingle().Which.Should().Be("value");
	}

	[TestMethod]
	[Description("Regression guard: verifies colon value syntax is parsed so command handlers can use --name:value form consistently with equals syntax.")]
	public void When_ParsingColonSyntax_Then_OptionValueIsCaptured()
	{
		var parsingOptions = new ParsingOptions
		{
			AllowUnknownOptions = false,
		};

		var parsed = InvocationOptionParser.Parse(
			["--output:json"],
			parsingOptions,
			knownOptionNames: ["output"]);

		parsed.HasErrors.Should().BeFalse();
		parsed.NamedOptions.Should().ContainKey("output");
		parsed.NamedOptions["output"].Should().ContainSingle().Which.Should().Be("json");
	}

	[TestMethod]
	[Description("Regression guard: verifies response-file tokens are expanded with quoting and comments so complex CLI invocations stay readable and deterministic.")]
	public void When_ResponseFileIsProvided_Then_TokensAreExpanded()
	{
		var parsingOptions = new ParsingOptions
		{
			AllowUnknownOptions = false,
			AllowResponseFiles = true,
		};
		var responseFile = Path.Combine(Path.GetTempPath(), $"repl-parser-{Guid.NewGuid():N}.rsp");
		File.WriteAllText(
			responseFile,
			"""
			--output json
			# comment line
			"two words"
			""");

		try
		{
			var parsed = InvocationOptionParser.Parse(
				[$"@{responseFile}"],
				parsingOptions,
				knownOptionNames: ["output"]);

			parsed.HasErrors.Should().BeFalse();
			parsed.NamedOptions.Should().ContainKey("output");
			parsed.NamedOptions["output"].Should().ContainSingle().Which.Should().Be("json");
			parsed.PositionalArguments.Should().ContainSingle().Which.Should().Be("two words");
		}
		finally
		{
			File.Delete(responseFile);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies missing response files are surfaced as parser diagnostics so users get actionable feedback on invalid @file inputs.")]
	public void When_ResponseFileDoesNotExist_Then_DiagnosticErrorIsProduced()
	{
		var parsingOptions = new ParsingOptions
		{
			AllowUnknownOptions = true,
			AllowResponseFiles = true,
		};
		var missingFile = Path.Combine(Path.GetTempPath(), $"repl-parser-missing-{Guid.NewGuid():N}.rsp");

		var parsed = InvocationOptionParser.Parse(
			[$"@{missingFile}"],
			parsingOptions,
			knownOptionNames: []);

		parsed.HasErrors.Should().BeTrue();
		parsed.Diagnostics.Should().ContainSingle();
		parsed.Diagnostics[0].Message.Should().Contain("Response file");
	}
}
