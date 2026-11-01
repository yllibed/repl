using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocsMcpSample;
using DocsMcpSample.Hosting;
using DocsMcpSample.Search;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Repl.Remote;

namespace Repl.DocsSampleTests;

[TestClass]
[AssetsAvailable]
public sealed class Given_DocumentHosting
{
	[TestMethod]
	[Description("The actual sample host exposes only search/fetch through MCP, rejects administrative calls, and isolates admin routes by listener rather than Host headers.")]
	public async Task When_ClientsUseBothListeners_Then_OnlyTheAdministrationPortCanReachTheConsole()
	{
		using var fixture = new TestCorpus();
		fixture.Index.Initialize();
		await fixture.WriteAsync("console.md", "# Console\n\nAttach to the console of a running process.").ConfigureAwait(false);
		await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
		var settings = fixture.Settings with { McpPort = AvailablePort(), AdminPort = AvailablePort() };
		var host = Server.Create(settings, services =>
		{
			services.AddSingleton<ITextEmbedding>(fixture.Embedding);
			services.AddSingleton(fixture.Index);
		});
		await using var hostOwner = host.ConfigureAwait(false);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		await host.StartAsync(deadline.Token).ConfigureAwait(false);
		try
		{
			using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settings.McpPort}") };
			await AssertPublicIsolationAsync(http, settings.AdminPort, deadline.Token).ConfigureAwait(false);
			using var adminPage = await http.GetAsync($"http://127.0.0.1:{settings.AdminPort}/", deadline.Token).ConfigureAwait(false);
			adminPage.EnsureSuccessStatusCode();
			(await adminPage.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)).Should().Contain("no authentication");
			var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = new Uri(http.BaseAddress, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
			}), cancellationToken: deadline.Token).ConfigureAwait(false);
			await using var clientOwner = client.ConfigureAwait(false);

			var tools = await client.ListToolsAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
			AssertPublicToolSchemas(tools);
			var search = await client.CallToolAsync("docs_search", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "console" }, cancellationToken: deadline.Token).ConfigureAwait(false);

			search.IsError.Should().NotBeTrue();
			var searchText = string.Join("", search.Content.OfType<TextContentBlock>().Select(content => content.Text));
			searchText.Should().NotContain("\u001b[");
			using var searchJson = JsonDocument.Parse(searchText);
			var hit = searchJson.RootElement[0];
			var identifier = hit.EnumerateObject().Single(property => property.Name.Equals("id", StringComparison.OrdinalIgnoreCase)).Value.GetString();
			var fetch = await client.CallToolAsync("docs_fetch", new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = identifier }, cancellationToken: deadline.Token).ConfigureAwait(false);
			fetch.IsError.Should().NotBeTrue();
			string.Join("", fetch.Content.OfType<TextContentBlock>().Select(content => content.Text)).Should().Contain("running process");
			foreach (var forbidden in new[] { "index_rebuild", "index_cancel", "sessions", "call_tool" })
			{
				var call = async () => await client.CallToolAsync(forbidden, cancellationToken: deadline.Token).ConfigureAwait(false);
				await call.Should().ThrowAsync<McpException>().ConfigureAwait(false);
			}
			using var socket = new ClientWebSocket();
			await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.AdminPort}/ws/repl"), deadline.Token).ConfigureAwait(false);
			await AssertConsoleStatusAsync(socket, deadline.Token).ConfigureAwait(false);
			host.Services.GetRequiredService<Sessions>().List().Should().Contain(session => session.Transport == "websocket");
			socket.Abort();
		}
		finally { await host.StopAsync(deadline.Token).ConfigureAwait(false); }
	}

	[TestMethod]
	[Description("Named-pipe one-shot commands observe the same generation as the HTTP host and return genuine process health exit codes.")]
	public async Task When_ALocalClientConnects_Then_ItUsesTheHostIndexAndHealthState()
	{
		using var fixture = new TestCorpus();
		fixture.Index.Initialize();
		await fixture.WriteAsync("console.md", "# Console\n\nShared process.").ConfigureAwait(false);
		var generation = await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
		var settings = fixture.Settings with { McpPort = AvailablePort(), AdminPort = AvailablePort() };
		var host = Server.Create(settings, services =>
		{
			services.AddSingleton<ITextEmbedding>(fixture.Embedding);
			services.AddSingleton(fixture.Index);
		});
		await using var hostOwner = host.ConfigureAwait(false);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		await host.StartAsync(deadline.Token).ConfigureAwait(false);
		try
		{
			var app = host.Services.GetRequiredService<ReplApp>();
			var listener = new NamedPipeReplServer(settings.Pipe, _ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(app, host.Services)));
			await using var listenerOwner = listener.ConfigureAwait(false);
			var client = await NamedPipeReplServer.ConnectAsync(settings.Pipe, deadline.Token).ConfigureAwait(false);
			await using var clientOwner = client.ConfigureAwait(false);
			await client.DiscoverAsync(deadline.Token).ConfigureAwait(false);

			var result = await client.ExecuteAsync(new() { Arguments = ["docs", "search", "--query", "console"] }, deadline.Token).ConfigureAwait(false);
			var health = await client.ExecuteAsync(new() { Arguments = ["health"] }, deadline.Token).ConfigureAwait(false);

			result.ExitCode.Should().Be(0);
			result.Result!.Value.GetRawText().Should().Contain("Shared process");
			health.ExitCode.Should().Be(0);
			fixture.Index.Generation()!.Id.Should().Be(generation.Id);
		}
		finally { await host.StopAsync(deadline.Token).ConfigureAwait(false); }
	}

	private static void AssertPublicToolSchemas(IEnumerable<McpClientTool> tools)
	{
		var catalog = tools.ToArray();
		catalog.Select(tool => tool.Name).Should().BeEquivalentTo("docs_search", "docs_fetch");
		catalog.Single(tool => string.Equals(tool.Name, "docs_search", StringComparison.Ordinal)).JsonSchema
			.GetProperty("properties").EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("query", "count");
		catalog.Single(tool => string.Equals(tool.Name, "docs_fetch", StringComparison.Ordinal)).JsonSchema
			.GetProperty("properties").EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("id", "maxChars");
	}

	private static int AvailablePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}

	[TestMethod]
	[Description("A foreign browser cannot administer the host; trusted clients can interrupt a watch, exit their session and reconnect while the shared host keeps running.")]
	public async Task When_AConsoleIsBusy_Then_AdminActionsStillUseTheSharedWorkerAndRejectForeignOrigins()
	{
		using var fixture = new TestCorpus();
		fixture.Index.Initialize();
		await fixture.WriteAsync("console.md", "# Console\n\nShared console.").ConfigureAwait(false);
		await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
		var settings = fixture.Settings with { McpPort = AvailablePort(), AdminPort = AvailablePort() };
		var host = Server.Create(settings, services => { services.AddSingleton<ITextEmbedding>(fixture.Embedding); services.AddSingleton(fixture.Index); });
		await using var hostOwner = host.ConfigureAwait(false);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		await host.StartAsync(deadline.Token).ConfigureAwait(false);
		try
		{
			using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settings.AdminPort}") };
			await AssertForeignOriginRejectedAsync(http, settings.AdminPort, deadline.Token).ConfigureAwait(false);
			http.DefaultRequestHeaders.Add("Origin", http.BaseAddress.GetLeftPart(UriPartial.Authority));
			using var socket = new ClientWebSocket();
			socket.Options.SetRequestHeader("Origin", http.BaseAddress.GetLeftPart(UriPartial.Authority));
			await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.AdminPort}/ws/repl"), deadline.Token).ConfigureAwait(false);
			await ReadUntilAsync(socket, ">", deadline.Token).ConfigureAwait(false);
			await socket.SendAsync(Encoding.UTF8.GetBytes("@@repl:hello {\"terminal\":\"xterm-256color\",\"cols\":100,\"rows\":30,\"ansi\":true,\"capabilities\":\"Ansi,VtInput,ResizeReporting\"}"), WebSocketMessageType.Text, endOfMessage: true, deadline.Token).ConfigureAwait(false);
			await socket.SendAsync(Encoding.UTF8.GetBytes("index watch\r"), WebSocketMessageType.Text, endOfMessage: true, deadline.Token).ConfigureAwait(false);
			await ReadUntilAsync(socket, "Serving", deadline.Token).ConfigureAwait(false);

			using var rebuilt = await http.PostAsJsonAsync("/admin/index/rebuild", new { }, deadline.Token).ConfigureAwait(false);
			rebuilt.EnsureSuccessStatusCode();
			(await rebuilt.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)).Should().Contain("Rebuild requested");
			await socket.SendAsync(new byte[] { 3 }, WebSocketMessageType.Text, endOfMessage: true, deadline.Token).ConfigureAwait(false);
			await ReadUntilAsync(socket, ">", deadline.Token).ConfigureAwait(false);
			await socket.SendAsync(Encoding.UTF8.GetBytes("health\r"), WebSocketMessageType.Text, endOfMessage: true, deadline.Token).ConfigureAwait(false);
			await ReadUntilAsync(socket, "Alive", deadline.Token).ConfigureAwait(false);
			await socket.SendAsync(Encoding.UTF8.GetBytes("sessions\r"), WebSocketMessageType.Text, endOfMessage: true, deadline.Token).ConfigureAwait(false);
			await ReadUntilAsync(socket, "websocket", deadline.Token).ConfigureAwait(false);
			await AssertConsoleExitAsync(socket, deadline.Token).ConfigureAwait(false);
			using var reconnected = new ClientWebSocket();
			await reconnected.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.AdminPort}/ws/repl"), deadline.Token).ConfigureAwait(false);
			await AssertConsoleStatusAsync(reconnected, deadline.Token).ConfigureAwait(false);
			reconnected.Abort();
		}
		finally { await host.StopAsync(deadline.Token).ConfigureAwait(false); }
	}

	private static async Task AssertConsoleExitAsync(ClientWebSocket socket, CancellationToken cancellationToken)
	{
		await socket.SendAsync(Encoding.UTF8.GetBytes("exit\r"), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
		var buffer = new byte[4096];
		WebSocketReceiveResult message;
		do
		{
			message = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
			Encoding.UTF8.GetString(buffer, 0, message.Count).Should().NotContain("Error:");
		} while (message.MessageType != WebSocketMessageType.Close);

		message.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
		await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken).ConfigureAwait(false);
	}

	private static async Task AssertPublicIsolationAsync(HttpClient http, int adminPort, CancellationToken cancellationToken)
	{
		foreach (var path in new[] { "/", "/admin/status", "/admin/compare?query=console", "/ws/repl", "/app.js", "/vendor/xterm.js" })
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, path);
			request.Headers.Host = $"127.0.0.1:{adminPort}";
			using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		}
	}

	private static async Task AssertForeignOriginRejectedAsync(HttpClient http, int adminPort, CancellationToken cancellationToken)
	{
		http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
		using var rejected = await http.PostAsJsonAsync("/admin/index/rebuild", new { }, cancellationToken).ConfigureAwait(false);
		rejected.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		using var socket = new ClientWebSocket();
		socket.Options.SetRequestHeader("Origin", "https://untrusted.example");
		var connect = () => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{adminPort}/ws/repl"), cancellationToken);
		await connect.Should().ThrowAsync<WebSocketException>().ConfigureAwait(false);
		http.DefaultRequestHeaders.Remove("Origin");
	}

	private static async Task AssertConsoleStatusAsync(ClientWebSocket socket, CancellationToken cancellationToken)
	{
		await ReadUntilAsync(socket, ">", cancellationToken).ConfigureAwait(false);
		await socket.SendAsync(Encoding.UTF8.GetBytes("?\r"), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
		var help = await ReadUntilAsync(socket, "exit", cancellationToken).ConfigureAwait(false);
		help.Should().Contain("Global Commands").And.NotContain("GeneratedAtUtc").And.NotContain("Name: docs");
		await socket.SendAsync(Encoding.UTF8.GetBytes("help docs search\r"), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
		var commandHelp = await ReadUntilAsync(socket, "--count", cancellationToken).ConfigureAwait(false);
		commandHelp.Should().Contain("Usage").And.Contain("docs search").And.NotContain("GeneratedAtUtc");
		await socket.SendAsync(Encoding.UTF8.GetBytes("index status\r"), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
		var output = await ReadUntilAsync(socket, "Serving", cancellationToken).ConfigureAwait(false);
		output.Should().NotContain("Error:");
	}

	private static async Task<string> ReadUntilAsync(ClientWebSocket socket, string expected, CancellationToken cancellationToken)
	{
		var buffer = new byte[4096];
		var text = new StringBuilder();
		var decoder = Encoding.UTF8.GetDecoder();
		var characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
		while (!text.ToString().Contains(expected, StringComparison.Ordinal))
		{
			WebSocketReceiveResult message;
			try { message = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false); }
			catch (OperationCanceledException error) { throw new TimeoutException($"Waiting for '{expected}'. Console output: {text}", error); }
			if (message.MessageType == WebSocketMessageType.Close) { Assert.Fail($"Console closed before '{expected}': {text}"); }
			text.Append(characters, 0, decoder.GetChars(buffer, 0, message.Count, characters, 0));
			if (text.Length > 100000 || text.ToString().Contains("Error:", StringComparison.Ordinal)) { Assert.Fail($"Unexpected console output: {text}"); }
		}
		return text.ToString();
	}
}
