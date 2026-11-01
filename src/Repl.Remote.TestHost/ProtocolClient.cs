using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Repl.Remote.TestHost;

// Deliberately independent of Repl.Remote and StreamJsonRpc, including their framing and DTOs.
internal static class ProtocolClient
{
	private static readonly string[] RequiredMethods = ["cli.describe", "cli.execute", "terminal.open"];
	private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	internal static async Task RunAsync(CancellationToken ct)
	{
		using var input = Console.OpenStandardInput();
		using var output = Console.OpenStandardOutput();
		var discovery = await CallAsync("rpc.discover", new { }, 1).ConfigureAwait(false);
		Require(discovery.GetProperty("id").GetInt32() == 1, "Integer request id was not preserved.");
		var profile = discovery.GetProperty("result");
		Require(profile.GetProperty("x-cli").GetProperty("version").GetString()!.StartsWith("1.", StringComparison.Ordinal), "Unexpected protocol profile.");
		var methods = profile.GetProperty("methods").EnumerateArray().Select(method => method.GetProperty("name").GetString()).ToArray();
		Require(RequiredMethods.All(name => methods.Contains(name, StringComparer.Ordinal)), "Discovery omitted a method.");
		var graph = await CallAsync("cli.describe", new { }, "graph").ConfigureAwait(false);
		Require(string.Equals(graph.GetProperty("id").GetString(), "graph", StringComparison.Ordinal), "String request id was not preserved.");
		Require(string.Equals(graph.GetProperty("result").GetProperty("name").GetString(), "raw-interop", StringComparison.Ordinal), "Unexpected graph.");
		var value = JsonSerializer.Deserialize<JsonElement>("""{"label":"été 🦆","items":[1,true,null,{"empty":[]}]}""");
		var invocation = await CallAsync("cli.execute", new { command = "echo", values = new { value } }, 3).ConfigureAwait(false);
		var result = invocation.GetProperty("result");
		Require(JsonElement.DeepEquals(result.GetProperty("result"), value), "Nested JSON or Unicode changed in transit.");
		Require(result.GetProperty("exitCode").GetInt32() == 17, "Command exit code was not preserved.");
		await ExpectErrorAsync("not.a.method", 4, -32601).ConfigureAwait(false);
		await ExpectErrorAsync("cli.execute", 5, -32602).ConfigureAwait(false);
		await ExpectErrorAsync("DisposeAsync", 6, -32601).ConfigureAwait(false);
		await Console.Error.WriteLineAsync("Independent JSON-RPC discovery, invocation and validation passed.").ConfigureAwait(false);

		async Task<JsonElement> CallAsync(string method, object parameters, object id)
		{
			var body = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters }, JsonOptions);
			var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
			await output.WriteAsync(header, ct).ConfigureAwait(false);
			await output.WriteAsync(body, ct).ConfigureAwait(false);
			await output.FlushAsync(ct).ConfigureAwait(false);
			var receivedHeader = new StringBuilder();
			var singleByte = new byte[1];
			while (!receivedHeader.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
			{
				Require(receivedHeader.Length < 8192, "Response header is too large.");
				await input.ReadExactlyAsync(singleByte, ct).ConfigureAwait(false);
				receivedHeader.Append((char)singleByte[0]);
			}
			var lengthHeader = receivedHeader.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
				.Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
			var length = int.Parse(lengthHeader.AsSpan("Content-Length:".Length), CultureInfo.InvariantCulture);
			Require(length is > 0 and <= 1024 * 1024, "Response length is invalid.");
			var response = new byte[length];
			await input.ReadExactlyAsync(response, ct).ConfigureAwait(false);
			return JsonSerializer.Deserialize<JsonElement>(response);
		}

		async Task ExpectErrorAsync(string method, int id, int code)
		{
			var response = await CallAsync(method, new { }, id).ConfigureAwait(false);
			Require(response.GetProperty("error").GetProperty("code").GetInt32() == code, $"Unexpected error for {method}.");
		}
	}

	private static void Require(bool condition, string message)
	{
		if (!condition) { throw new InvalidOperationException(message); }
	}
}
