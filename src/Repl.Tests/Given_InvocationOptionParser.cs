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
		var responseFile = Path.Join(Path.GetTempPath(), $"repl-parser-{Guid.NewGuid():N}.rsp");
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
		var missingFile = Path.Join(Path.GetTempPath(), $"repl-parser-missing-{Guid.NewGuid():N}.rsp");

		var parsed = InvocationOptionParser.Parse(
			[$"@{missingFile}"],
			parsingOptions,
			knownOptionNames: []);

		parsed.HasErrors.Should().BeTrue();
		parsed.Diagnostics.Should().ContainSingle();
		parsed.Diagnostics[0].Message.Should().Contain("Response file");
	}

	[TestMethod]
	[Description("Regression guard: verifies an empty inline option name with equals syntax is rejected so malformed '--=value' tokens cannot silently bind.")]
	public void When_ParsingEqualsWithEmptyOptionName_Then_DiagnosticErrorIsProduced()
	{
		var parsingOptions = new ParsingOptions
		{
			AllowUnknownOptions = false,
		};

		var parsed = InvocationOptionParser.Parse(
			["--=value"],
			parsingOptions,
			knownOptionNames: ["output"]);

		parsed.HasErrors.Should().BeTrue();
		parsed.Diagnostics.Should().ContainSingle();
		parsed.Diagnostics[0].Message.Should().Contain("Unknown option '--'");
	}

	[TestMethod]
	[Description("Regression guard: verifies an empty inline option name with colon syntax is rejected so malformed '--:value' tokens cannot silently bind.")]
	public void When_ParsingColonWithEmptyOptionName_Then_DiagnosticErrorIsProduced()
	{
		var parsingOptions = new ParsingOptions
		{
			AllowUnknownOptions = false,
		};

		var parsed = InvocationOptionParser.Parse(
			["--:value"],
			parsingOptions,
			knownOptionNames: ["output"]);

		parsed.HasErrors.Should().BeTrue();
		parsed.Diagnostics.Should().ContainSingle();
		parsed.Diagnostics[0].Message.Should().Contain("Unknown option '--'");
	}

	[TestMethod]
	[Description("Regression guard: verifies a standalone '@' token is treated as positional input so non-response-file literals are preserved.")]
	public void When_TokenIsStandaloneAtSign_Then_TokenRemainsPositional()
	{
		var parsed = InvocationOptionParser.Parse(
			["@","plain"],
			new ParsingOptions { AllowUnknownOptions = true, AllowResponseFiles = true },
			knownOptionNames: []);

		parsed.HasErrors.Should().BeFalse();
		parsed.PositionalArguments.Should().Equal("@", "plain");
	}

	[TestMethod]
	[Description("Regression guard: verifies empty response files expand to no tokens so @file indirection is a no-op when file content is empty.")]
	public void When_ResponseFileIsEmpty_Then_NoTokensAreInjected()
	{
		var parsingOptions = new ParsingOptions
		{
			AllowUnknownOptions = true,
			AllowResponseFiles = true,
		};
		var responseFile = Path.Join(Path.GetTempPath(), $"repl-parser-empty-{Guid.NewGuid():N}.rsp");
		File.WriteAllText(responseFile, string.Empty);

		try
		{
			var parsed = InvocationOptionParser.Parse(
				[$"@{responseFile}"],
				parsingOptions,
				knownOptionNames: []);

			parsed.HasErrors.Should().BeFalse();
			parsed.NamedOptions.Should().BeEmpty();
			parsed.PositionalArguments.Should().BeEmpty();
		}
		finally
		{
			File.Delete(responseFile);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies UTF-8 BOM response files parse correctly so first token is not polluted by BOM bytes.")]
	public void When_ResponseFileHasUtf8Bom_Then_FirstTokenParsesNormally()
	{
		var parsingOptions = new ParsingOptions
		{
			AllowUnknownOptions = false,
			AllowResponseFiles = true,
		};
		var responseFile = Path.Join(Path.GetTempPath(), $"repl-parser-bom-{Guid.NewGuid():N}.rsp");
		var contentBytes = new byte[] { 0xEF, 0xBB, 0xBF }
			.Concat(System.Text.Encoding.UTF8.GetBytes("--output json"))
			.ToArray();
		File.WriteAllBytes(responseFile, contentBytes);

		try
		{
			var parsed = InvocationOptionParser.Parse(
				[$"@{responseFile}"],
				parsingOptions,
				knownOptionNames: ["output"]);

			parsed.HasErrors.Should().BeFalse();
			parsed.NamedOptions.Should().ContainKey("output");
			parsed.NamedOptions["output"].Should().ContainSingle().Which.Should().Be("json");
		}
		finally
		{
			File.Delete(responseFile);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies trailing escape in response files emits a warning so silent token corruption is surfaced to callers.")]
	public void When_ResponseFileEndsWithEscape_Then_WarningDiagnosticIsProduced()
	{
		var parsingOptions = new ParsingOptions
		{
			AllowUnknownOptions = true,
			AllowResponseFiles = true,
		};
		var responseFile = Path.Join(Path.GetTempPath(), $"repl-parser-escape-{Guid.NewGuid():N}.rsp");
		File.WriteAllText(responseFile, "value\\");

		try
		{
			var parsed = InvocationOptionParser.Parse(
				[$"@{responseFile}"],
				parsingOptions,
				knownOptionNames: []);

			parsed.Diagnostics.Should().ContainSingle();
			parsed.Diagnostics[0].Severity.Should().Be(ParseDiagnosticSeverity.Warning);
		}
		finally
		{
			File.Delete(responseFile);
		}
	}
}
