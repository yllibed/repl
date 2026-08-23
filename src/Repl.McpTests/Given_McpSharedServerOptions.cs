using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// Regressions for the documented multi-connection host pattern: build
/// <c>BuildMcpServerOptions()</c> ONCE and create an <see cref="McpServer"/> per connection
/// (docs/mcp-transports.md). That path bypasses <see cref="McpServerHandler"/>'s request handlers
/// entirely — the SDK dispatches straight into the pre-built primitives — so nothing that relies on
/// the handler prologue applies to it.
/// </summary>
[TestClass]
public sealed class Given_McpSharedServerOptions
{
	[TestMethod]
	[Description("Guards capability resolution on the documented reusable-options path: two connections created from ONE BuildMcpServerOptions() result must each observe their OWN client's capabilities. The pre-built primitives never run the handler's request prologue, so without per-invocation request binding a sampling-capable client is told sampling is unavailable — the capability is resolved against nothing at all.")]
	public async Task When_TwoConnectionsShareOneOptionsInstance_Then_CapabilitiesAreRequestScoped()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		// Distinct tokens, not "supported"/"not-supported": the tool result is JSON, so the text block
		// carries quotes, and a substring assertion on the shorter word would match both answers.
		app.Map("probe", (IMcpSampling sampling) => sampling.IsSupported ? "sampling-on" : "sampling-off");

		// Built once and reused across connections, exactly as docs/mcp-transports.md prescribes.
		var mcpOptions = app.BuildMcpServerOptions();
		using var cts = new CancellationTokenSource();

		var capable = await StartAsync(mcpOptions, BuildSamplingClientOptions(), cts.Token).ConfigureAwait(false);
		await using var capableScope = capable.ConfigureAwait(false);
		var plain = await StartAsync(mcpOptions, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var plainScope = plain.ConfigureAwait(false);

		capable.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);
		plain.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var capableText = await CallProbeAsync(capable, cts.Token).ConfigureAwait(false);
		var plainText = await CallProbeAsync(plain, cts.Token).ConfigureAwait(false);

		capableText.Should().Contain("sampling-on");
		plainText.Should().Contain("sampling-off");
	}

	private static Task<string> CallProbeAsync(McpPipeSession session, CancellationToken cancellationToken) =>
		CallAsync(session, "probe", cancellationToken);

	private static async Task<string> CallAsync(
		McpPipeSession session,
		string toolName,
		CancellationToken cancellationToken)
	{
		var result = await session.Client.CallToolAsync(
			toolName: toolName,
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cancellationToken).ConfigureAwait(false);

		return result.Content.OfType<TextContentBlock>().First().Text;
	}

	/// <summary>
	/// Starts one connection over the shared <paramref name="mcpOptions"/>, mirroring the sample in
	/// docs/mcp-transports.md — including passing no service provider to <c>McpServer.Create</c>.
	/// </summary>
	private static Task<McpPipeSession> StartAsync(
		McpServerOptions mcpOptions,
		McpClientOptions? clientOptions,
		CancellationToken cancellationToken) =>
		McpPipeSession.StartAsync(
			async (io, token) =>
			{
				var transport = new StreamServerTransport(io.InputStream, io.OutputStream, "shared-options-server");
				var server = McpServer.Create(transport, mcpOptions);
				try
				{
					await server.RunAsync(token).ConfigureAwait(false);
				}
				finally
				{
					await server.DisposeAsync().ConfigureAwait(false);
					await transport.DisposeAsync().ConfigureAwait(false);
				}
			},
			clientOptions,
			cancellationToken);

	// Sampling is deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005) but still supported by
	// Repl.Mcp until the SDK removes the surface (#51).
#pragma warning disable MCP9005
	private static McpClientOptions BuildSamplingClientOptions() => new()
	{
		Capabilities = new ClientCapabilities { Sampling = new SamplingCapability() },
		Handlers = new McpClientHandlers
		{
			SamplingHandler = static (request, _, _) => ValueTask.FromResult(new CreateMessageResult
			{
				Content = [new TextContentBlock { Text = "ga" }],
				Model = "test-model",
			}),
		},
	};
#pragma warning restore MCP9005
}
