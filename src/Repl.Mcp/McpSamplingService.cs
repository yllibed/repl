using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Roots, Sampling, and Logging are deprecated by MCP spec 2026-07-28 (SEP-2577, SDK
// diagnostic MCP9005) with no replacement API; hosts still rely on them, so Repl keeps
// supporting the features until the SDK removes them. Tracked in issue #51.
#pragma warning disable MCP9005

namespace Repl.Mcp;

/// <summary>
/// Internal implementation of <see cref="IMcpSampling"/> backed by a live <see cref="McpServer"/> session.
/// </summary>
internal sealed class McpSamplingService : IMcpSampling
{
	private McpServer? _server;

	public bool IsSupported => _server?.ClientCapabilities?.Sampling is not null;

	public async ValueTask<string?> SampleAsync(
		string prompt,
		int maxTokens = 1024,
		CancellationToken cancellationToken = default)
	{
		if (!IsSupported)
		{
			return null;
		}

		var result = await _server!.SampleAsync(
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

	internal void AttachServer(McpServer server) => _server = server;
}
