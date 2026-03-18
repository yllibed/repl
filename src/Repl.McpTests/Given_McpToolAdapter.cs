using Repl.Mcp;

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
	[Description("Interaction prefills become --answer:name=value tokens.")]
	public void When_AnswerPrefixes_Then_BecomeAnswerTokens()
	{
		var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
		{
			["answer:confirm"] = "yes",
		};

		var tokens = McpToolAdapter.ReconstructTokens("deploy", args);

		tokens.Should().BeEquivalentTo(["deploy", "--answer:confirm=yes"]);
	}

	[TestMethod]
	[Description("Mixed route args, options, and prefills reconstruct correctly.")]
	public void When_MixedArguments_Then_ReconstructedCorrectly()
	{
		var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
		{
			["id"] = "42",
			["verbose"] = "true",
			["answer:confirm"] = "yes",
		};

		var tokens = McpToolAdapter.ReconstructTokens("contact {id:int} delete", args);

		tokens.Should().BeEquivalentTo(["contact", "42", "delete", "--verbose", "true", "--answer:confirm=yes"]);
	}
}
