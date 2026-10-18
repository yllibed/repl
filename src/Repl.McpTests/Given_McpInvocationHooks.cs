using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpInvocationHooks
{
	[TestMethod]
	public async Task When_ToolArgumentsAreRewritten_Then_HooksAndHandlerKeepNativeCollectionsAndScope()
	{
		Marker? hookService = null;
		string[]? bound = null;
		var calls = 0;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("search", (string[] items, Marker service) =>
			{
				bound = items;
				service.Should().BeSameAs(hookService);
				return "ok";
			});
			app.UseInvocationHook((ReplInvocationContext context, Marker service) =>
			{
				calls++;
				hookService = service;
				context.Channel.Should().Be(ReplInvocationChannel.Mcp);
				context.Parsed.TryGetValue<string[]>("items", out var items).Should().BeTrue();
				items.Should().Equal("", "--help", "with spaces");
				return ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("search",
					new Dictionary<string, object?>(StringComparer.Ordinal) { ["items"] = Array.Empty<string>() }));
			});
		}, services => services.AddScoped<Marker>());
		string[] submitted = ["", "--help", "with spaces"];
		var result = await fixture.Client.CallToolAsync("search", new Dictionary<string, object?>(StringComparer.Ordinal) { ["items"] = submitted });
		result.IsError.Should().NotBeTrue("{0}", System.Text.Json.JsonSerializer.Serialize(result));
		calls.Should().Be(1);
		bound.Should().BeEmpty();
	}

	[TestMethod]
	public async Task When_ClientSuppliesAnUndeclaredField_Then_ItIsRejectedBeforeHooks()
	{
		var calls = 0;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("run", () => "unused");
			app.UseInvocationHook(() => { calls++; return ReplInvocationResult.Handled("must not run"); });
		});
		var result = await fixture.Client.CallToolAsync("run", new Dictionary<string, object?>(StringComparer.Ordinal) { ["secret"] = "value" });
		result.IsError.Should().BeTrue();
		calls.Should().Be(0);
	}

	[TestMethod]
	public async Task When_ToolHookHandlesAnUnbindableValue_Then_TheHandlerDoesNotRun()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("run", (int count) => { Assert.Fail("Handled requests do not dispatch."); return "unused"; });
			app.UseInvocationHook((ReplInvocationContext context) =>
			{
				context.Parsed.Values["count"].State.Should().Be(InputValueState.Invalid);
				return ReplInvocationResult.Handled("external action");
			});
		});
		var result = await fixture.Client.CallToolAsync("run", new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = "invalid" });
		result.IsError.Should().NotBeTrue("{0}", System.Text.Json.JsonSerializer.Serialize(result));
	}

	private sealed class Marker;

	[TestMethod]
	[DataRow("true")]
	[DataRow("false")]
	[DataRow("null")]
	public async Task When_NoHookReceivesJsonScalarsAsStrings_Then_PreviousCoercionIsPreserved(string json)
	{
		await using var fixture = await McpTestFixture.CreateAsync(app => app.Map("run", (string value, string[] items, ScalarOptions options) =>
		{
			value.Should().Be(json);
			items.Should().Equal(json);
			options.Scalar.Should().Be(json);
			options.Collection.Should().Equal("true", "false");
			return "ok";
		}));
		var value = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
		var flags = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("[true,false]");
		var result = await fixture.Client.CallToolAsync("run", new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["value"] = value, ["items"] = value, ["scalar"] = value, ["collection"] = flags,
		});
		result.IsError.Should().NotBeTrue("{0}", System.Text.Json.JsonSerializer.Serialize(result));
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_McpStartsAPublicChild_Then_PagingStaysWithItsOwningInvocation(bool childFails)
	{
		var childRan = false;
		var parentRan = false;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("outer", (IReplPagingContext paging) => { paging.Cursor.Should().Be("parent"); parentRan = true; return "ok"; });
			app.Map("inner", (IReplPagingContext paging) =>
			{
				paging.Cursor.Should().BeNull();
				childRan = true;
				if (childFails) { throw new InvalidOperationException("child failure"); }
			});
			app.UseInvocationHook(async (ReplInvocationContext context) =>
			{
				if (string.Equals(context.Parsed.Command, "outer", StringComparison.Ordinal))
				{
					(await app.Core.RunAsync(ReplInvocationRequest.ForCommand("inner")).ConfigureAwait(false)).Should().Be(childFails ? 1 : 0);
				}
				else
				{
					context.Parsed.Input.Arguments.Should().NotContain("--output:json").And.NotContain(ReplResultFlowOptionNames.Cursor);
				}
				return ReplInvocationResult.Continue();
			});
		});
		var result = await fixture.Client.CallToolAsync("outer", new Dictionary<string, object?>(StringComparer.Ordinal) { ["_replCursor"] = "parent" });
		result.IsError.Should().NotBeTrue("{0}", System.Text.Json.JsonSerializer.Serialize(result));
		childRan.Should().BeTrue();
		parentRan.Should().BeTrue();
	}

	[Repl.Parameters.ReplOptionsGroup]
	private sealed class ScalarOptions
	{
		public string? Scalar { get; set; }
		public string[] Collection { get; set; } = [];
	}
}
