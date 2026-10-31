using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Repl.Interaction;
using Repl.Remote;
using Repl.Parameters;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_ReplAppBackend
{
	[TestMethod]
	[Description("Native JSON arrays bind through invocation hooks and DI, with handler output separated from the returned JSON value.")]
	public async Task When_AStructuredRequestRuns_Then_TheOrdinaryPipelineIsUsed()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create(services => services.AddScoped<Marker>());
		var hookCalled = false;
		app.UseInvocationHook((ReplInvocationContext context, int[] items, Marker marker) =>
		{
			context.Channel.Should().Be(ReplInvocationChannel.Programmatic);
			items.Should().Equal(1, 2);
			marker.Should().NotBeNull();
			hookCalled = true;
			return ReplInvocationResult.Continue();
		});
		app.Map("sum", async (int[] items, Marker marker, IReplIoContext io) =>
		{
			await io.Output.WriteAsync("side output").ConfigureAwait(false);
			await io.Error.WriteAsync("diagnostic").ConfigureAwait(false);
			return new { total = items.Sum(), marker = marker.Id };
		});
		await using var backend = new ReplAppRemoteBackend(app);

		var result = await backend.ExecuteAsync(new() { Command = "sum", Values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
		{
			["items"] = JsonSerializer.SerializeToElement((int[])[1, 2]),
		}, }, deadline.Token);

		hookCalled.Should().BeTrue();
		result.ExitCode.Should().Be(0);
		result.Result!.Value.GetProperty("total").GetInt32().Should().Be(3);
		result.StandardOutput.Should().Be("side output");
		result.StandardError.Should().Be("diagnostic");
	}

	[TestMethod]
	[Description("Remote execution preserves module exit codes and the completed-with-failures outcome introduced on main.")]
	public async Task When_AModuleReportsFailures_Then_ItsExitPolicySurvivesTheAdapter()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create();
		app.MapModule(new Checks());
		await using var backend = new ReplAppRemoteBackend(app);

		var result = await backend.ExecuteAsync(new() { Arguments = ["check"] }, deadline.Token);

		result.ExitCode.Should().Be(23);
		result.Kind.Should().Be("completedWithFailures");
		result.Result!.Value.GetString().Should().Be("one failed");
	}

	[TestMethod]
	[Description("Discovery excludes injected services while retaining canonical parameter names, collection schemas and command aliases.")]
	public async Task When_TheGraphIsDescribed_Then_ItCanDriveNativeInvocation()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create(services => services.AddScoped<Marker>());
		app.Map("sum", (int[] items, [FromServices] Marker marker) => items.Length + marker.Id.Length).WithAlias("add");
		await using var backend = new ReplAppRemoteBackend(app);

		var graph = await backend.DescribeAsync(deadline.Token);
		var command = graph.Commands.Single(item => string.Equals(item.Path, "sum", StringComparison.Ordinal));

		command.Aliases.Should().Contain("add");
		command.Parameters.Should().ContainSingle().Which.Name.Should().Be("items");
		command.Parameters[0].Schema.GetProperty("type").GetString().Should().Be("array");
		command.Parameters[0].Schema.GetProperty("items").GetProperty("type").GetString().Should().Be("integer");
	}

	[TestMethod]
	[Description("Discovery and native binding agree on explicit null scalars, nullable array elements and global option types.")]
	public async Task When_NullableValuesAreExposed_Then_TheirSchemasPermitNull()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create().UseGlobalOptions<Globals>();
		app.Map("nullable", (int? count, int?[] items, Globals globals) => new { count, items, tenant = globals.Tenant });
		await using var backend = new ReplAppRemoteBackend(app);

		var graph = await backend.DescribeAsync(deadline.Token);
		var parameters = graph.Commands.Single(command => string.Equals(command.Path, "nullable", StringComparison.Ordinal)).Parameters;
		parameters.Single(parameter => string.Equals(parameter.Name, "count", StringComparison.Ordinal)).Schema.GetProperty("anyOf")[1].GetProperty("type").GetString().Should().Be("null");
		parameters.Single(parameter => string.Equals(parameter.Name, "items", StringComparison.Ordinal)).Schema.GetProperty("items").GetProperty("anyOf")[1].GetProperty("type").GetString().Should().Be("null");
		graph.GlobalParameters.Single(parameter => string.Equals(parameter.Name, "tenant", StringComparison.Ordinal)).Schema.GetProperty("type").GetString().Should().Be("string");
		var result = await backend.ExecuteAsync(new() { Command = "nullable", Values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
		{
			["count"] = JsonSerializer.SerializeToElement<int?>(value: null),
			["items"] = JsonSerializer.SerializeToElement((int?[])[null, 2]),
		}, GlobalValues = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["tenant"] = JsonSerializer.SerializeToElement("demo") }, }, deadline.Token);

		result.ExitCode.Should().Be(0);
		result.Result!.Value.GetProperty("count").ValueKind.Should().Be(JsonValueKind.Null);
		result.Result.Value.GetProperty("items")[0].ValueKind.Should().Be(JsonValueKind.Null);
		result.Result.Value.GetProperty("tenant").GetString().Should().Be("demo");
	}

	[TestMethod]
	[Description("Programmatic presence predicates see the same channel and transport during discovery and execution.")]
	public async Task When_AModuleDependsOnTheChannel_Then_DiscoveryMatchesExecution()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create();
		app.MapModule(new Checks(), static context => context.Channel == ReplRuntimeChannel.Programmatic &&
			string.Equals(context.SessionInfo.TransportName, "remote-cli", StringComparison.Ordinal));
		await using var backend = new ReplAppRemoteBackend(app);

		var graph = await backend.DescribeAsync(deadline.Token);
		var result = await backend.ExecuteAsync(new() { Arguments = ["check"] }, deadline.Token);

		graph.Commands.Should().Contain(command => command.Path == "check");
		result.ExitCode.Should().Be(23);
	}

	[TestMethod]
	[Description("A legacy token rewrite retains the forced JSON contract and routes preprocessing feedback away from the structured result.")]
	public async Task When_AnArgumentPreprocessorEmitsFeedback_Then_TheJsonResultStaysSeparate()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create();
		app.Map("echo {text}", (string text) => text);
		app.UseArgumentPreprocessor(async (input, ct) =>
		{
			if (input.Arguments.Count == 0 || !string.Equals(input.Arguments[0], "/legacy", StringComparison.Ordinal))
			{
				return ArgumentPreprocessingResult.Continue();
			}
			await app.Services.GetRequiredService<IReplInteractionChannel>().WriteStatusAsync("Legacy alias expanded.", ct).ConfigureAwait(false);
			return ArgumentPreprocessingResult.Replace(["echo", "rewritten", .. input.Arguments.Skip(1)]);
		});
		await using var backend = new ReplAppRemoteBackend(app);

		var result = await backend.ExecuteAsync(new() { Arguments = ["/legacy"] }, deadline.Token);

		result.ExitCode.Should().Be(0);
		result.Result!.Value.GetString().Should().Be("rewritten");
		result.StandardError.Should().Contain("Legacy alias expanded.");
	}

	[TestMethod]
	[DataRow("root")]
	[DataRow("context")]
	[DataRow("explicit")]
	[Description("Bare invocations and contexts retain their ordinary text-help outcome over the wire, while explicit JSON help stays structured.")]
	public async Task When_HelpIsRequested_Then_ItDoesNotBecomeABackendFailure(string mode)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create();
		app.Map("version", () => "one");
		app.Context("tools", tools => tools.Map("status", () => "ready"));
		var name = TestPipeName.Create();
		await using var endpoint = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(app)));
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);

		string[] arguments = mode switch { "context" => ["tools"], "explicit" => ["--help"], _ => [] };
		var result = await client.ExecuteAsync(new() { Arguments = arguments }, deadline.Token);

		result.ExitCode.Should().Be(0);
		result.Kind.Should().Be("help");
		if (string.Equals(mode, "explicit", StringComparison.Ordinal)) { result.Result.Should().NotBeNull(); }
		else
		{
			result.Result.Should().BeNull();
			result.StandardOutput.Should().Contain(string.Equals(mode, "context", StringComparison.Ordinal) ? "status" : "version");
		}
	}

	[TestMethod]
	[Description("Nested JSON objects and heterogeneous arrays reach object handlers as native dictionaries and arrays rather than boxed JSON nodes.")]
	public async Task When_AnObjectIsSubmitted_Then_ItMatchesTheNativeInvocationContract()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create();
		object? received = null;
		app.Map("payload", (object payload) => { received = payload; return payload; });
		var name = TestPipeName.Create();
		await using var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(app)));
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);
		var value = JsonSerializer.SerializeToElement(new { x = 1, nested = new { label = "été" }, items = (object?[])[true, null, "text"] });

		var result = await client.ExecuteAsync(new() { Command = "payload", Values = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["payload"] = value } }, deadline.Token);

		result.ExitCode.Should().Be(0);
		received.Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>();
		result.Result!.Value.GetProperty("nested").GetProperty("label").GetString().Should().Be("été");
		result.Result.Value.GetProperty("items")[1].ValueKind.Should().Be(JsonValueKind.Null);
	}

	[TestMethod]
	[DataRow("array")]
	[DataRow("list")]
	[DataRow("ilist")]
	[DataRow("collection")]
	[DataRow("readonlycollection")]
	[DataRow("readonlylist")]
	[DataRow("enumerable")]
	[Description("Every advertised list-like collection accepts the same native JSON array, including empty arrays, without string tokenization.")]
	public async Task When_ACollectionIsAdvertised_Then_JsonArraysCanInvokeIt(string route)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create();
		app.Map("array", (int[] numbers) => numbers.Sum());
		app.Map("list", (List<int> numbers) => numbers.Sum());
		app.Map("ilist", (IList<int> numbers) => numbers.Sum());
		app.Map("collection", (ICollection<int> numbers) => numbers.Sum());
		app.Map("readonlycollection", (IReadOnlyCollection<int> numbers) => numbers.Sum());
		app.Map("readonlylist", (IReadOnlyList<int> numbers) => numbers.Sum());
		app.Map("enumerable", (IEnumerable<int> numbers) => numbers.Sum());
		var name = TestPipeName.Create();
		await using var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(app)));
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);
		var graph = await client.DescribeAsync(deadline.Token);
		graph.Commands.Single(command => string.Equals(command.Path, route, StringComparison.Ordinal)).Parameters.Single().Schema.GetProperty("type").GetString().Should().Be("array");

		foreach (var numbers in new int[][] { [1, 2], [] })
		{
			var result = await client.ExecuteAsync(new() { Command = route, Values = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["numbers"] = JsonSerializer.SerializeToElement(numbers) } }, deadline.Token);
			result.ExitCode.Should().Be(0);
			result.Result!.Value.GetInt32().Should().Be(numbers.Sum());
		}
	}

	public sealed class Globals
	{
		public string Tenant { get; set; } = "default";
	}

	private sealed class Marker { public string Id { get; } = Guid.NewGuid().ToString("N"); }
	private sealed class Checks : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { CompletedWithFailures = 23 };
		public void Map(IReplMap map) => map.Map("check", () => Results.CompletedWithFailures("one failed"));
	}
}
