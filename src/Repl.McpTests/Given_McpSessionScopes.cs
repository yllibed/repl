using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpSessionScopes
{
	private sealed class ScopedProbe
	{
		public Guid Id { get; } = Guid.NewGuid();
	}

	private sealed class DisposableProbe(List<Guid> disposed) : IDisposable
	{
		public Guid Id { get; } = Guid.NewGuid();

		public void Dispose() => disposed.Add(Id);
	}

	[TestMethod]
	[Description("Probe for the MCP half of per-session DI: the SDK creates an AsyncServiceScope per request handler invocation (McpServerOptions.ScopeRequests defaults to true) and exposes it as RequestContext.Services. Repl discarded it, so a Scoped service resolved from the application root and every tool call on every connection shared one instance.")]
	public async Task When_TwoToolCallsShareAConnection_Then_EachResolvesItsOwnScopedInstance()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("scoped", (ScopedProbe probe) => probe.Id.ToString()),
			services => services.AddScoped<ScopedProbe>()).ConfigureAwait(false);

		var first = await CallAsync(fixture, "scoped").ConfigureAwait(false);
		var second = await CallAsync(fixture, "scoped").ConfigureAwait(false);

		first.Should().NotBeNullOrWhiteSpace();
		second.Should().NotBeNullOrWhiteSpace();
		first.Should().NotBe(second, "each MCP invocation is its own scope");
	}

	[TestMethod]
	[Description("Guards that the per-invocation scope is DISPOSED, not merely created: a Scoped IDisposable resolved by a tool call must be released when that call ends rather than accumulating for the life of the server process. The SDK disposes the scope it opened in a finally, so this holds only while Repl resolves from that scope instead of from the application root.")]
	public async Task When_AToolCallResolvesAScopedDisposable_Then_ItIsDisposedWhenTheCallEnds()
	{
		var disposed = new List<Guid>();
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("disposable", (DisposableProbe probe) => probe.Id.ToString()),
			services => services.AddScoped(_ => new DisposableProbe(disposed))).ConfigureAwait(false);

		var reported = await CallAsync(fixture, "disposable").ConfigureAwait(false);

		reported.Should().NotBeNullOrWhiteSpace();
		disposed.Should().ContainSingle(
			"the scope the SDK opened for the call is disposed when the call returns");
		reported.Should().Contain(
			disposed[0].ToString(),
			"the instance disposed must be the one the command was handed");
	}

	[TestMethod]
	[Description("Guards the composition ORDER, which is the invariant this change could break silently. The per-request scope descends from the application root and carries none of the session's services, so they are re-applied on top of it. Get that backwards and a command injecting IMcpClientRoots resolves nothing — and, because McpServiceProviderOverlay also answers IServiceProviderIsService, a prompt declaring one is reclassified as taking a client-supplied argument and stops being invocable at all.")]
	public async Task When_ACommandInjectsACapabilityService_Then_ItStillResolvesThroughTheRequestScope()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map(
				"capability",
				(IMcpClientRoots roots, IMcpFeedback feedback, ScopedProbe probe) =>
					$"{roots is not null}|{feedback is not null}|{probe.Id != Guid.Empty}"),
			services => services.AddScoped<ScopedProbe>()).ConfigureAwait(false);

		var reported = await CallAsync(fixture, "capability").ConfigureAwait(false);

		reported.Should().Contain("True|True|True");
	}

	[TestMethod]
	[Description("Guards the MCP half of issue #70: IReplSessionState is documented as per-session, but on MCP it fell through the session overlay to the application root, so two concurrent connections shared one bag. It is now session-owned, which is also what keeps the initialize-era dynamic-tools story working — a command writes it and the next tools/list on THAT connection sees it.")]
	public async Task When_TwoConnectionsShareOneServer_Then_NeitherReadsTheOtherSessionState()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("remember {value}", (IReplSessionState state, string value) =>
		{
			state.Set("carried", value);
			return "stored";
		});
		app.Map("recall", (IReplSessionState state) => state.Get<string>("carried") ?? "(none)");

		var options = new ReplMcpServerOptions { TransportFactory = McpTestFixture.PipeTransportFactory };
		var handler = new McpServerHandler(app.Core, options, app.Services);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var first = await McpPipeSession
			.StartAsync(handler.RunAsync, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var firstScope = first.ConfigureAwait(false);
		var second = await McpPipeSession
			.StartAsync(handler.RunAsync, clientOptions: null, cts.Token).ConfigureAwait(false);
		await using var secondScope = second.ConfigureAwait(false);

		await CallAsync(first, "remember", new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["value"] = "alpha",
		}, cts.Token).ConfigureAwait(false);

		var carried = await CallAsync(first, "recall", cancellationToken: cts.Token).ConfigureAwait(false);
		var other = await CallAsync(second, "recall", cancellationToken: cts.Token).ConfigureAwait(false);

		carried.Should().Contain("alpha", "session state survives across calls on its own connection");
		other.Should().Contain("(none)", "another connection is another session");
	}

	private static Task<string> CallAsync(McpTestFixture fixture, string tool) =>
		CallAsync(fixture.Client, tool, arguments: null, CancellationToken.None);

	private static Task<string> CallAsync(
		McpPipeSession session,
		string tool,
		Dictionary<string, object?>? arguments = null,
		CancellationToken cancellationToken = default) =>
		CallAsync(session.Client, tool, arguments, cancellationToken);

	private static async Task<string> CallAsync(
		ModelContextProtocol.Client.McpClient client,
		string tool,
		Dictionary<string, object?>? arguments,
		CancellationToken cancellationToken)
	{
		var result = await client.CallToolAsync(
			toolName: tool,
			arguments: arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal),
			cancellationToken: cancellationToken).ConfigureAwait(false);
		return result.Content.OfType<TextContentBlock>().First().Text;
	}
}
