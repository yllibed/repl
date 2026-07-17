using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Roots, Sampling, and Logging are deprecated by MCP spec 2026-07-28 (SEP-2577, SDK
// diagnostic MCP9005); the designated successor for server-initiated flows (SEP-2322,
// multi-round-trip requests) is not yet consumable in the SDK and hosts still rely on
// these features, so Repl keeps supporting them until the SDK removes the surface (#51).
#pragma warning disable MCP9005

namespace Repl.Mcp;

/// <summary>
/// Internal implementation of <see cref="IMcpSampling"/> backed by a live <see cref="McpServer"/> session.
/// </summary>
internal sealed class McpSamplingService(McpRequestServerAccessor servers) : IMcpSampling
{
	public bool IsSupported => servers.Effective?.ClientCapabilities?.Sampling is not null;

	public async ValueTask<string?> SampleAsync(
		string prompt,
		int maxTokens = 1024,
		CancellationToken cancellationToken = default)
	{
		// Single read: the effective server must not change between the support check and
		// the call (a concurrent request re-binding the accessor must not be observed).
		if (servers.Effective is not { ClientCapabilities.Sampling: not null } server)
		{
			return null;
		}

		var result = await server.SampleAsync(
			new CreateMessageRequestParams
			{
				Messages =
				[
					new SamplingMessage
					{
						Role = Role.User,
						Content = [new TextContentBlock { Text = prompt }],
					},
				],
				MaxTokens = maxTokens,
			},
			cancellationToken).ConfigureAwait(false);

		return result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
	}

}
