using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// Repl.Mcp absorbs three failures by design: a tool failure's detail is withheld from the client, a
/// failed catalog build is hidden behind the previous catalog, and a failed roots prime is swallowed.
/// Each must still reach the operator's log — once per episode, not once per request that retries it.
/// </summary>
[TestClass]
public sealed class Given_McpDiagnostics
{
	// The category and event ids are what an operator filters on, so the tests pin them rather than
	// matching any entry that happens to carry an exception.
	private const string Category = "Repl.Mcp";
	private const int ToolFailureWithheldEvent = 2001;
	private const int StaleCatalogServedEvent = 2002;
	private const int RootsPrimeFailedEvent = 2003;
	private const int StaleCatalogStillServedEvent = 2004;
	private const int CatalogRecoveredEvent = 2005;
	private const int RootsPrimeStillFailingEvent = 2006;

	[TestMethod]
	[Description("A tool call that fails with a withheld detail (an unhandled exception, not McpException/McpInteractionException) must still record that detail for the operator: the client's generic 'Command failed' message is deliberate, discarding invocation.Failure and its rendered text entirely is not.")]
	public async Task When_AToolFailureDetailIsWithheld_Then_TheOperatorSinkRecordsIt()
	{
		var provider = new CapturingLoggerProvider();
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("boom", object () => throw new InvalidOperationException("secret-detail")),
			services => CaptureLogs(services, provider)).ConfigureAwait(false);

		var result = await CallAsync(fixture.Client, "boom").ConfigureAwait(false);

		result.Should().NotContain("secret-detail", "the withheld detail must not reach the remote client");
		var entry = provider.Single(ToolFailureWithheldEvent);
		entry.Category.Should().Be(Category);
		entry.Level.Should().Be(LogLevel.Error);
		entry.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("secret-detail");
		entry.Message.Should().Contain("secret-detail", "the rendered detail the client did not get belongs in the message");
	}

	[TestMethod]
	[Description("Roots priming swallows everything but the caller's own cancellation by design: every execution entry point primes, and a handler that never reads roots must not fail because the client could not answer. The failure must still reach the operator — but a fast failure is retried on every tool call, so only the first of an episode warns and the repeats go to Debug.")]
	public async Task When_RootsPrimingKeepsFailing_Then_OnlyTheFirstFailureWarns()
	{
		var provider = new CapturingLoggerProvider();
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("touch", () => "ok"),
			configureOptions: null,
			clientOptions: CreateBrokenRootsClient(),
			configureServices: services => CaptureLogs(services, provider)).ConfigureAwait(false);

		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);

		var warning = provider.Single(RootsPrimeFailedEvent);
		warning.Category.Should().Be(Category);
		warning.Level.Should().Be(LogLevel.Warning);
		warning.Exception.Should().BeOfType<UriFormatException>("the client answered roots/list with an unparseable URI");
		provider.Count(RootsPrimeStillFailingEvent).Should().Be(0, "the first failure of an episode is the one that warns");

		await CallAsync(fixture.Client, "touch").ConfigureAwait(false);

		provider.Count(RootsPrimeFailedEvent).Should().Be(1, "a repeat of the same episode must not warn again");
		provider.Count(RootsPrimeStillFailingEvent).Should().Be(1, "the repeat is still recorded, at Debug");
	}

	[TestMethod]
	[Description("The availability fallback re-serves an initialize-era connection's previous catalog when a build fails, and republishes it stale, so every request retries the build. The failure must reach the operator — once, as a warning, rather than once per request for as long as it lasts — and the end of the episode must be visible too, or a quiet log would read as 'still failing'.")]
	public async Task When_AStaleCatalogIsServedUntilRecovery_Then_ItWarnsOnceAndLogsTheRecovery()
	{
		var provider = new CapturingLoggerProvider();
		var app = ReplApp.Create(services => CaptureLogs(services, provider));
		app.UseMcpServer();
		app.Map("initial", () => "ok");
		var options = new ReplMcpServerOptions { TransportFactory = McpTestFixture.PipeTransportFactory };
		var handler = new McpServerHandler(app.Core, options, app.Services);

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var session = await McpPipeSession.StartAsync(
			handler.RunAsync,
			new McpClientOptions { ProtocolVersion = McpProtocolRevisions.LastWithSessions },
			cts.Token).ConfigureAwait(false);
		await using var sessionScope = session.ConfigureAwait(false);

		await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
		options.CommandFilter = _ => throw new InvalidOperationException("projection-boom");
		app.Core.InvalidateRouting();

		var stale = await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		stale.Should().ContainSingle(
			tool => string.Equals(tool.Name, "initial", StringComparison.Ordinal),
			"the fallback itself is unchanged: the previous catalog is still served");
		var warning = provider.Single(StaleCatalogServedEvent);
		warning.Category.Should().Be(Category);
		warning.Level.Should().Be(LogLevel.Warning);
		warning.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("projection-boom");
		provider.Count(StaleCatalogStillServedEvent).Should().Be(0, "the first failure of an episode is the one that warns");

		await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		provider.Count(StaleCatalogServedEvent).Should().Be(1, "a retry of the same failing version must not warn again");
		provider.Count(StaleCatalogStillServedEvent).Should().BeGreaterThan(0, "the retry's failure is still recorded, at Debug");
		provider.Count(CatalogRecoveredEvent).Should().Be(0, "nothing has recovered yet");

		options.CommandFilter = null;
		await session.Client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

		provider.Single(CatalogRecoveredEvent).Level.Should().Be(LogLevel.Information);
		provider.Count(StaleCatalogServedEvent).Should().Be(1, "recovering must not re-warn the episode it just ended");
	}

	private static void CaptureLogs(IServiceCollection services, CapturingLoggerProvider provider) =>
		services.AddLogging(builder =>
		{
			builder.ClearProviders();
			builder.SetMinimumLevel(LogLevel.Debug);
			builder.AddProvider(provider);
		});

	private static McpClientOptions CreateBrokenRootsClient()
	{
		// Roots is deprecated by MCP spec 2026-07-28 (SEP-2577), but hosts still use it and Repl keeps
		// supporting it until the SDK removes the surface (#51).
#pragma warning disable MCP9005
		return new McpClientOptions
		{
			Capabilities = new ClientCapabilities { Roots = new RootsCapability() },
			Handlers = new McpClientHandlers
			{
				// An unparseable URI fails server-side while being mapped — a real fetch failure that needs
				// no client-side throw, which would escape the SDK's own message loop instead.
				RootsHandler = static (_, _) => ValueTask.FromResult(new ListRootsResult
				{
					Roots = [new Root { Uri = "http://", Name = "invalid" }],
				}),
			},
		};
#pragma warning restore MCP9005
	}

	private static async Task<string> CallAsync(McpClient client, string tool)
	{
		var result = await client.CallToolAsync(
			toolName: tool,
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);
		return result.Content.OfType<TextContentBlock>().First().Text;
	}

	// Concurrent because the server logs from its own threads while the test reads from another.
	private sealed class CapturingLoggerProvider : ILoggerProvider
	{
		private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

		public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

		public CapturedLogEntry Single(int eventId) => _entries.Should().ContainSingle(entry => entry.EventId == eventId).Subject;

		public int Count(int eventId) => _entries.Count(entry => entry.EventId == eventId);

		public void Dispose()
		{
		}
	}

	private sealed class CapturingLogger(string categoryName, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			entries.Enqueue(new CapturedLogEntry(categoryName, logLevel, eventId.Id, formatter(state, exception), exception));
	}

	private sealed record CapturedLogEntry(string Category, LogLevel Level, int EventId, string Message, Exception? Exception);
}
