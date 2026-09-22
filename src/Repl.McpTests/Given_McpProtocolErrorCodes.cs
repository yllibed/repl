using ModelContextProtocol;
using ModelContextProtocol.Client;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// An unknown resource or prompt is a protocol error with a standard code, not an internal error:
/// hosts branch on the code to tell "gone" from "broken" (SEP-2164). The codes follow the SDK's own
/// handlers, so a Repl server answers exactly as a server built on the SDK alone would.
/// </summary>
[TestClass]
public sealed class Given_McpProtocolErrorCodes
{
	[TestMethod]
	[Description("An initialize-era client reading an unknown resource URI gets the legacy ResourceNotFound code (-32002), which is what the SDK's own resource handler returns on revisions before 2026-07-28. It used to get a code-less McpException, surfaced as InternalError (-32603).")]
	public async Task When_ReadingUnknownResource_Then_ResourceNotFoundCodeIsReturned()
	{
		var error = await ReadUnknownResourceAsync(McpProtocolRevisions.LastWithSessions).ConfigureAwait(false);

		error.ErrorCode.Should().Be(McpErrorCode.ResourceNotFound);
	}

	[TestMethod]
	[Description("On 2026-07-28 an unresolvable resource URI is reported with the standard JSON-RPC InvalidParams (-32602), not the legacy ResourceNotFound — the SDK selects between the two by negotiated revision, and hard-coding the legacy code would answer a modern client with a code its revision no longer uses.")]
	public async Task When_ReadingUnknownResourceOnTheSessionlessRevision_Then_InvalidParamsCodeIsReturned()
	{
		var error = await ReadUnknownResourceAsync(McpProtocolRevisions.Sessionless).ConfigureAwait(false);

		error.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
	}

	[TestMethod]
	[DataRow(McpProtocolRevisions.LastWithSessions)]
	[DataRow(McpProtocolRevisions.Sessionless)]
	[Description("prompts/get with an unknown name is InvalidParams (-32602) on every revision, as the SDK's own prompt handler answers it. It used to surface as InternalError (-32603).")]
	public async Task When_GettingUnknownPrompt_Then_InvalidParamsCodeIsReturned(string protocolVersion)
	{
		await using var fixture = await CreateFixtureAsync(protocolVersion).ConfigureAwait(false);

		var act = async () => await fixture.Client.GetPromptAsync("missing").ConfigureAwait(false);

		var error = await act.Should().ThrowAsync<McpProtocolException>().ConfigureAwait(false);
		error.Which.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
	}

	private static async Task<McpProtocolException> ReadUnknownResourceAsync(string protocolVersion)
	{
		var fixture = await CreateFixtureAsync(protocolVersion).ConfigureAwait(false);
		await using (fixture.ConfigureAwait(false))
		{
			var act = async () => await fixture.Client.ReadResourceAsync("repl://missing").ConfigureAwait(false);

			return (await act.Should().ThrowAsync<McpProtocolException>().ConfigureAwait(false)).Which;
		}
	}

	private static Task<McpTestFixture> CreateFixtureAsync(string protocolVersion) =>
		McpTestFixture.CreateAsync(
			app => app.Map("status", () => "ok").ReadOnly(),
			configureOptions: null,
			clientOptions: new McpClientOptions { ProtocolVersion = protocolVersion });
}
