using ModelContextProtocol;
using ModelContextProtocol.Client;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// The JSON-RPC error an unknown resource or prompt is answered with, on each protocol revision.
/// <c>McpServerHandler.ReadResourceAsync</c> and <c>GetPromptAsync</c> explain the choice of codes.
/// </summary>
[TestClass]
public sealed class Given_McpProtocolErrorCodes
{
	private const string UnknownResourceUri = "repl://missing";

	[TestMethod]
	[Description("An initialize-era client reading an unknown resource URI gets the legacy ResourceNotFound code (-32002), which is what the SDK's own resource handler returns on revisions before 2026-07-28. It used to get a code-less McpException, surfaced as InternalError (-32603).")]
	public async Task When_LegacyClientReadsAnUnknownResource_Then_ResourceNotFoundCodeIsReturned()
	{
		var error = await ReadUnknownResourceAsync(McpProtocolRevisions.LastWithSessions).ConfigureAwait(false);

		error.ErrorCode.Should().Be(McpErrorCode.ResourceNotFound);
	}

	[TestMethod]
	[Description("On 2026-07-28 an unresolvable resource URI is reported with the standard JSON-RPC InvalidParams (-32602), not the legacy ResourceNotFound — the SDK selects between the two by negotiated revision, and hard-coding the legacy code would answer a modern client with a code its revision no longer uses.")]
	public async Task When_ModernClientReadsAnUnknownResource_Then_InvalidParamsCodeIsReturned()
	{
		var error = await ReadUnknownResourceAsync(McpProtocolRevisions.Sessionless).ConfigureAwait(false);

		error.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
	}

	[TestMethod]
	[DataRow(McpProtocolRevisions.LastWithSessions)]
	[DataRow(McpProtocolRevisions.Sessionless)]
	[Description("SEP-2164 says the error's data SHOULD carry the URI that was not found, and both revisions' specifications show it there. The SDK client copies the data's properties into the exception's Data.")]
	public async Task When_ReadingUnknownResource_Then_TheErrorDataNamesTheUri(string protocolVersion)
	{
		var error = await ReadUnknownResourceAsync(protocolVersion).ConfigureAwait(false);

		error.Data["uri"].Should().Be(UnknownResourceUri);
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
			var act = async () => await fixture.Client.ReadResourceAsync(UnknownResourceUri).ConfigureAwait(false);

			return (await act.Should().ThrowAsync<McpProtocolException>().ConfigureAwait(false)).Which;
		}
	}

	private static Task<McpTestFixture> CreateFixtureAsync(string protocolVersion) =>
		McpTestFixture.CreateAsync(
			app => app.Map("status", () => "ok").ReadOnly(),
			configureOptions: null,
			clientOptions: new McpClientOptions { ProtocolVersion = protocolVersion });
}
