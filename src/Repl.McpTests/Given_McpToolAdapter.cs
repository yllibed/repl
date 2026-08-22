using Microsoft.Extensions.DependencyInjection;
using Repl;
using Repl.Mcp;
using Repl.Documentation;
using Repl.Internal.Options;
using Repl.Parameters;
using System.Text.Json;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpToolAdapter
{
	[TestMethod]
	[Description("Literal segments pass through unchanged.")]
	public void When_LiteralSegments_Then_PassedThrough()
	{
		var tokens = McpToolAdapter.ReconstructTokens(
			"contact add",
			new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

		tokens.Should().BeEquivalentTo(["contact", "add"]);
	}

	[TestMethod]
	[Description("Dynamic segments are substituted from arguments.")]
	public void When_DynamicSegment_Then_SubstitutedFromArguments()
	{
		var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
		{
			["id"] = "abc123",
		};

		var tokens = McpToolAdapter.ReconstructTokens("client {id} show", args);

		tokens.Should().BeEquivalentTo(["client", "abc123", "show"]);
	}

	[TestMethod]
	[Description("Constrained dynamic segments are substituted.")]
	public void When_ConstrainedDynamicSegment_Then_Substituted()
	{
		var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
		{
			["id"] = "550e8400-e29b-41d4-a716-446655440000",
		};

		var tokens = McpToolAdapter.ReconstructTokens("contact {id:guid} show", args);

		tokens.Should().BeEquivalentTo(["contact", "550e8400-e29b-41d4-a716-446655440000", "show"]);
	}

	[TestMethod]
	[Description("Non-route arguments become named options.")]
	public void When_ExtraArguments_Then_BecomeNamedOptions()
	{
		var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
		{
			["format"] = "json",
		};

		var tokens = McpToolAdapter.ReconstructTokens("status", args);

		tokens.Should().BeEquivalentTo(["status", "--format", "json"]);
	}

	[TestMethod]
	[Description("ReconstructTokens treats all non-route args as named options (answer: separation happens upstream in PrepareExecution).")]
	public void When_RemainingArgs_Then_AllBecomeNamedOptions()
	{
		var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
		{
			["format"] = "json",
			["verbose"] = "true",
		};

		var tokens = McpToolAdapter.ReconstructTokens("deploy", args);

		tokens.Should().BeEquivalentTo(["deploy", "--format", "json", "--verbose", "true"]);
	}

	[TestMethod]
	[Description("Mixed route args and options reconstruct correctly.")]
	public void When_MixedArguments_Then_ReconstructedCorrectly()
	{
		var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
		{
			["id"] = "42",
			["verbose"] = "true",
		};

		var tokens = McpToolAdapter.ReconstructTokens("contact {id:int} delete", args);

		tokens.Should().BeEquivalentTo(["contact", "42", "delete", "--verbose", "true"]);
	}

	[TestMethod]
	[Description("PrepareExecution accepts compact opaque result cursors and emits them as result-flow tokens.")]
	public void When_ResultCursorIsValid_Then_ResultFlowTokenIsEmitted()
	{
		var (tokens, _) = McpToolAdapter.PrepareExecution(
			CreatePagedCommand("contacts"),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.Cursor] = JsonSerializer.SerializeToElement("abc_DEF-123"),
			});

		tokens.Should().ContainInOrder("--result:cursor", "abc_DEF-123", "contacts");
	}

	[TestMethod]
	[Description("PrepareExecution rejects result cursors that could be confused with CLI token boundaries.")]
	public void When_ResultCursorContainsWhitespace_Then_Rejected()
	{
		var action = () => McpToolAdapter.PrepareExecution(
			CreatePagedCommand("contacts"),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.Cursor] = JsonSerializer.SerializeToElement("abc def"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*cursor*whitespace*");
	}

	[TestMethod]
	[Description("PrepareExecution rejects result cursors that start like CLI options.")]
	public void When_ResultCursorStartsWithDash_Then_Rejected()
	{
		var action = () => McpToolAdapter.PrepareExecution(
			CreatePagedCommand("contacts"),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.Cursor] = JsonSerializer.SerializeToElement("--result:all"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*cursor*option*");
	}

	[TestMethod]
	[Description("PrepareExecution rejects overly large result cursors.")]
	public void When_ResultCursorIsTooLong_Then_Rejected()
	{
		var action = () => McpToolAdapter.PrepareExecution(
			CreatePagedCommand("contacts"),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.Cursor] = JsonSerializer.SerializeToElement(new string('a', 513)),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*cursor*512*");
	}

	[TestMethod]
	[Description("PrepareExecution rejects result cursors that contain control characters.")]
	public void When_ResultCursorContainsControlCharacter_Then_Rejected()
	{
		var action = () => McpToolAdapter.PrepareExecution(
			CreatePagedCommand("contacts"),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.Cursor] = JsonSerializer.SerializeToElement("abc\u001b[2J"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*cursor*control*");
	}

	[TestMethod]
	[Description("PrepareExecution accepts compact numeric result page sizes and emits them as result-flow tokens.")]
	public void When_ResultPageSizeIsValid_Then_ResultFlowTokenIsEmitted()
	{
		var (tokens, _) = McpToolAdapter.PrepareExecution(
			CreatePagedCommand("contacts"),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.PageSize] = JsonSerializer.SerializeToElement(25),
			});

		tokens.Should().ContainInOrder("--result:page-size", "25", "contacts");
	}

	[TestMethod]
	[Description("PrepareExecution rejects result page sizes that are not numeric.")]
	public void When_ResultPageSizeIsNotNumeric_Then_Rejected()
	{
		var action = () => McpToolAdapter.PrepareExecution(
			CreatePagedCommand("contacts"),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.PageSize] = JsonSerializer.SerializeToElement("abc"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*page size*numeric*");
	}

	[TestMethod]
	[Description("PrepareExecution rejects overly large result page size tokens.")]
	public void When_ResultPageSizeTokenIsTooLong_Then_Rejected()
	{
		var action = () => McpToolAdapter.PrepareExecution(
			CreatePagedCommand("contacts"),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.PageSize] = JsonSerializer.SerializeToElement(new string('1', 11)),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*page size*10*");
	}

	[TestMethod]
	[Description("PrepareExecution rejects result page sizes that do not fit in a positive Int32.")]
	public void When_ResultPageSizeOverflowsInt32_Then_Rejected()
	{
		var action = () => McpToolAdapter.PrepareExecution(
			CreatePagedCommand("contacts"),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.PageSize] = JsonSerializer.SerializeToElement("9999999999"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*page size*32-bit*");
	}

	[TestMethod]
	[Description("PrepareExecution treats reserved result-flow argument names case-insensitively.")]
	public void When_ResultFlowInputsUseDifferentCase_Then_ReservedDispatchStillValidatesAndEmitsTokens()
	{
		var (tokens, _) = McpToolAdapter.PrepareExecution(
			CreatePagedCommand("contacts"),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["_replcursor"] = JsonSerializer.SerializeToElement("abc_DEF-123"),
				["_replpagesize"] = JsonSerializer.SerializeToElement(25),
			});

		tokens.Should().ContainInOrder("--result:cursor", "abc_DEF-123", "--result:page-size", "25", "contacts");
		tokens.Should().NotContain("--_replcursor");
		tokens.Should().NotContain("--_replpagesize");
	}

	[TestMethod]
	[Description("PrepareExecution rejects MCP arguments that are not declared by the command schema.")]
	public void When_ArgumentIsNotInToolSchema_Then_Rejected()
	{
		var command = new ReplDocCommand(
			Path: "contacts {id}",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [new ReplDocArgument("id", "string", Required: true, Description: null)],
			Options: [new ReplDocOption("format", "string", Required: false, Description: null, Aliases: [], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)]);

		var action = () => McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["id"] = JsonSerializer.SerializeToElement("abc"),
				["output:xml"] = JsonSerializer.SerializeToElement("true"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*not defined*schema*");
	}

	[TestMethod]
	[Description("A differently-cased MCP key remains backwards-compatible when it identifies one schema option unambiguously, including custom short-token reconstruction.")]
	public void When_McpOptionNameUsesDifferentCaseAndHasOneMatch_Then_ConfiguredTokenIsRetained()
	{
		var command = new ReplDocCommand(
			Path: "deploy",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options: [new ReplDocOption("tenant", "string", Required: false, Description: null, Aliases: ["-t"], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)]);

		var (tokens, _) = McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["TENANT"] = JsonSerializer.SerializeToElement("acme"),
			});

		tokens.Should().Equal("deploy", "-t", "acme");
	}

	[TestMethod]
	[Description("For a single advertised field, submitting both its exact spelling and a unique case-insensitive fallback is rejected as a duplicate canonical field instead of silently overwriting one value.")]
	public void When_ExactAndCaseFallbackNameTheSameMcpField_Then_RejectedAsDuplicate()
	{
		var command = new ReplDocCommand(
			Path: "deploy",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options: [new ReplDocOption("tenant", "string", Required: false, Description: null, Aliases: ["-t"], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)]);
		var action = () => McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["tenant"] = JsonSerializer.SerializeToElement("south"),
				["TENANT"] = JsonSerializer.SerializeToElement("north"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*TENANT*duplicates*tenant*");
	}

	[TestMethod]
	[Description("Case-sensitive option names that differ only by casing preserve both exact MCP fields and reconstruct each distinct CLI token in one invocation.")]
	public void When_OptionNamesDifferOnlyByCase_Then_BothExactMcpFieldsRemainDistinct()
	{
		var command = CreateCaseDistinctOptionsCommand();

		var (tokens, _) = McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["tenant"] = JsonSerializer.SerializeToElement("south"),
				["TENANT"] = JsonSerializer.SerializeToElement("north"),
			});

		tokens.Should().Equal("deploy", "--tenant", "south", "--TENANT", "north");
	}

	[TestMethod]
	[Description("A non-exact MCP field casing is rejected when multiple advertised fields match it case-insensitively.")]
	public void When_NonExactMcpFieldMatchesMultipleCaseDistinctOptions_Then_RejectedAsAmbiguous()
	{
		var action = () => McpToolAdapter.PrepareExecution(
			CreateCaseDistinctOptionsCommand(),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["Tenant"] = JsonSerializer.SerializeToElement("acme"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*ambiguous*tenant*TENANT*");
	}

	[TestMethod]
	[Description("Case-distinct ordinary fields retain command provenance beside exact synthetic cursor and page-size fields instead of being reclassified as result-flow controls.")]
	public void When_CaseDistinctOptionsMatchResultFlowNames_Then_EachFieldKeepsItsOwnSink()
	{
		var command = new ReplDocCommand(
			Path: "contacts",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options:
			[
				new ReplDocOption("_replcursor", "string", Required: false, Description: null, Aliases: ["--_replcursor"], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null),
				new ReplDocOption("_replpagesize", "string", Required: false, Description: null, Aliases: ["--_replpagesize"], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null),
			],
			AcceptsPagingInput: true);

		var (tokens, prefills) = McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.Cursor] = JsonSerializer.SerializeToElement("opaque"),
				["_replcursor"] = JsonSerializer.SerializeToElement("ordinary-cursor"),
				[McpResultFlowArgumentNames.PageSize] = JsonSerializer.SerializeToElement(25),
				["_replpagesize"] = JsonSerializer.SerializeToElement("ordinary-size"),
			});

		tokens.Should().Equal(
			ReplResultFlowOptionNames.Cursor, "opaque",
			ReplResultFlowOptionNames.PageSize, "25",
			"contacts", "--_replcursor", "ordinary-cursor", "--_replpagesize", "ordinary-size");
		prefills.Should().BeEmpty();
	}

	[TestMethod]
	[Description("A declared answer and a case-distinct ordinary option under the answer prefix keep separate prefill and CLI destinations.")]
	public void When_CaseDistinctOptionMatchesAnswerField_Then_DeclaredProvenanceControlsDispatch()
	{
		var command = new ReplDocCommand(
			Path: "wizard",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options: [new ReplDocOption("answer.CONFIRM", "string", Required: false, Description: null, Aliases: ["--answer.CONFIRM"], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)],
			Answers: [new ReplDocAnswer("confirm", "bool", Description: null)]);

		var (tokens, prefills) = McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["answer.confirm"] = JsonSerializer.SerializeToElement(value: false),
				["answer.CONFIRM"] = JsonSerializer.SerializeToElement("ordinary"),
			});

		tokens.Should().Equal("wizard", "--answer.CONFIRM", "ordinary");
		prefills.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("confirm", "false"));
	}

	[TestMethod]
	[Description("PrepareExecution rejects token-like MCP argument values before reconstructing CLI tokens.")]
	public void When_ArgumentValueStartsWithDashDash_Then_Rejected()
	{
		var command = new ReplDocCommand(
			Path: "contacts",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options: [new ReplDocOption("format", "string", Required: false, Description: null, Aliases: [], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)]);

		var action = () => McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["format"] = JsonSerializer.SerializeToElement("--result:all"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*argument value*CLI option*");
	}

	[TestMethod]
	[Description("PrepareExecution rejects answer prefills that are not declared by the command schema.")]
	public void When_AnswerPrefillIsNotInToolSchema_Then_Rejected()
	{
		var command = new ReplDocCommand(
			Path: "wizard",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options: []);

		var action = () => McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["answer.confirm"] = JsonSerializer.SerializeToElement("yes"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*not defined*schema*");
	}

	[TestMethod]
	[Description("PrepareExecution rejects result-flow inputs for commands that do not expose paging in their schema.")]
	public void When_ResultFlowInputIsNotInToolSchema_Then_Rejected()
	{
		var command = new ReplDocCommand(
			Path: "contacts",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options: []);

		var action = () => McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.Cursor] = JsonSerializer.SerializeToElement("abc123"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*not defined*schema*");
	}

	[TestMethod]
	[Description("A bool option's value is embedded as a single inline '--name=value' token instead of a '--name' / 'value' pair, so it can never be split apart and re-lexed as a fresh option by the downstream parser.")]
	public void When_BoolOptionValueIsReconstructed_Then_EmbeddedAsSingleInlineToken()
	{
		var command = new ReplDocCommand(
			Path: "deploy",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options: [new ReplDocOption("verbose", "bool", Required: false, Description: null, Aliases: [], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)]);

		var (tokens, _) = McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["verbose"] = JsonSerializer.SerializeToElement("true"),
			});

		tokens.Should().Equal("deploy", "--verbose=true");
	}

	[TestMethod]
	[Description("Nullable bool options use the same inline token boundary as bool options, so an MCP string value can never be re-lexed as a separate hidden CLI alias.")]
	public void When_NullableBoolOptionValueIsReconstructed_Then_EmbeddedAsSingleInlineToken()
	{
		var command = new ReplDocCommand(
			Path: "deploy",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options: [new ReplDocOption("verbose", "bool?", Required: false, Description: null, Aliases: [], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)]);

		var (tokens, _) = McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["verbose"] = JsonSerializer.SerializeToElement("-t=denim"),
			});

		tokens.Should().Equal("deploy", "--verbose=-t=denim");
	}

	[TestMethod]
	[Description("PrepareExecution rejects positional route-segment values that look like a CLI option token: unlike an option, a positional segment has no separator that can escape the value, so it would be re-lexed as a fresh option once substituted into the token stream.")]
	public void When_PositionalArgumentValueLooksLikeOptionToken_Then_Rejected()
	{
		var command = new ReplDocCommand(
			Path: "contacts {id}",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [new ReplDocArgument("id", "string", Required: true, Description: null)],
			Options: []);

		var action = () => McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["id"] = JsonSerializer.SerializeToElement("-t=denim"),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*argument value*CLI option*positional*");
	}

	[TestMethod]
	[Description("A signed numeric literal remains a valid positional route-segment value: it starts with a dash but IsSignedNumericLiteral carves it out, matching the CLI parser's own positional-vs-option rule.")]
	public void When_PositionalArgumentValueIsSignedNumericLiteral_Then_Accepted()
	{
		var command = new ReplDocCommand(
			Path: "contacts {id}",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [new ReplDocArgument("id", "string", Required: true, Description: null)],
			Options: []);

		var (tokens, _) = McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["id"] = JsonSerializer.SerializeToElement("-42"),
			});

		tokens.Should().Equal("contacts", "-42");
	}

	[TestMethod]
	[Description(
		"End-to-end regression for the option-smuggling vulnerability: a tool call supplies a value " +
		"for a visible bool option that itself looks like '-t=denim', an alias of a Hidden() option on " +
		"the same route. Before the fix, ReconstructTokens emitted the value as its own token, which the " +
		"bool flag declined to consume without a diagnostic (that decline is required so legitimate " +
		"flag-chaining like '--verbose --other' keeps working) and which the parser then re-lexed as a " +
		"fresh '-t' option, binding the hidden 'tenant' target. The inline '--verbose=-t=denim' token this " +
		"test asserts on cannot be split apart that way, so 'tenant' must never appear as bound.")]
	public void When_ToolCallValueLooksLikeHiddenOptionToken_Then_ItIsNotBoundAsAnOption()
	{
		var command = new ReplDocCommand(
			Path: "deploy",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options: [new ReplDocOption("verbose", "bool", Required: false, Description: null, Aliases: [], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)]);

		var (tokens, _) = McpToolAdapter.PrepareExecution(
			command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["verbose"] = JsonSerializer.SerializeToElement("-t=denim"),
			});

		tokens.Should().Equal("deploy", "--verbose=-t=denim");

		var schema = new OptionSchema(
			[
				new OptionSchemaEntry("--verbose", "verbose", OptionSchemaTokenKind.BoolFlag, ReplArity.ZeroOrOne),
				new OptionSchemaEntry("-t", "tenant", OptionSchemaTokenKind.NamedOption, ReplArity.ZeroOrOne, IsHidden: true),
			],
			new Dictionary<string, OptionSchemaParameter>(StringComparer.OrdinalIgnoreCase)
			{
				["verbose"] = new OptionSchemaParameter("verbose", typeof(bool), ReplParameterMode.OptionOnly),
				["tenant"] = new OptionSchemaParameter("tenant", typeof(string), ReplParameterMode.OptionOnly, IsHidden: true),
			},
			ReplCaseSensitivity.CaseSensitive);

		var parseResult = InvocationOptionParser.Parse(
			[.. tokens.Skip(1)],
			schema,
			new ParsingOptions());

		parseResult.NamedOptions.Should().NotContainKey("tenant");
		parseResult.NamedOptions.Should().ContainKey("verbose");
		parseResult.NamedOptions["verbose"].Should().ContainSingle().Which.Should().Be("-t=denim");
	}

	[TestMethod]
	[Description("An MCP string value that names a response file remains literal during the programmatic invocation, so file contents cannot inject a hidden route option.")]
	public async Task When_StringToolValueNamesResponseFile_Then_ProgrammaticInvocationDoesNotExpandIt()
	{
		var responseFile = Path.Join(Path.GetTempPath(), $"repl-mcp-review-{Guid.NewGuid():N}.rsp");
		File.WriteAllText(responseFile, "ordinaryValue --hidden secret");
		try
		{
			string? observedVisible = null;
			string? observedHidden = null;
			var app = ReplApp.Create();
			app.Map(
					"deploy",
					([ReplOption] string? visible, [ReplOption] string? hidden) =>
					{
						observedVisible = visible;
						observedHidden = hidden;
						return "ok";
					})
				.WithOption("hidden", static option => option.Hidden());
			await using var services = new ServiceCollection().BuildServiceProvider();
			var adapter = new McpToolAdapter(
				app.Core, new ReplMcpServerOptions(), services, new McpRequestServerAccessor());
			adapter.RegisterRoute(
				"deploy",
				new ReplDocCommand(
					Path: "deploy",
					Description: null,
					Aliases: [],
					IsHidden: false,
					Arguments: [],
					Options: [new ReplDocOption("visible", "string", Required: false, Description: null, Aliases: [], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)]));

			var result = await adapter.InvokeAsync(
				"deploy",
				new Dictionary<string, JsonElement>(StringComparer.Ordinal)
				{
					["visible"] = JsonSerializer.SerializeToElement($"@{responseFile}"),
				},
				server: null,
				progressToken: null,
				CancellationToken.None);

			result.IsError.Should().NotBeTrue();
			observedHidden.Should().BeNull();
			observedVisible.Should().Be($"@{responseFile}");
		}
		finally
		{
			File.Delete(responseFile);
		}
	}

	private static ReplDocCommand CreateCaseDistinctOptionsCommand() =>
		new(
			Path: "deploy",
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options:
			[
				new ReplDocOption("tenant", "string", Required: false, Description: null, Aliases: ["--tenant"], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null),
				new ReplDocOption("TENANT", "string", Required: false, Description: null, Aliases: ["--TENANT"], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null),
			]);

	private static ReplDocCommand CreatePagedCommand(string path) =>
		new(
			Path: path,
			Description: null,
			Aliases: [],
			IsHidden: false,
			Arguments: [],
			Options: [],
			AcceptsPagingInput: true);
}
