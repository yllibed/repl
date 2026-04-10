using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
