using Repl.Mcp;
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
			"contacts",
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
			"contacts",
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
			"contacts",
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
			"contacts",
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.Cursor] = JsonSerializer.SerializeToElement(new string('a', 513)),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*cursor*512*");
	}

	[TestMethod]
	[Description("PrepareExecution accepts compact numeric result page sizes and emits them as result-flow tokens.")]
	public void When_ResultPageSizeIsValid_Then_ResultFlowTokenIsEmitted()
	{
		var (tokens, _) = McpToolAdapter.PrepareExecution(
			"contacts",
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
			"contacts",
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
			"contacts",
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[McpResultFlowArgumentNames.PageSize] = JsonSerializer.SerializeToElement(new string('1', 21)),
			});

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*page size*20*");
	}
}
