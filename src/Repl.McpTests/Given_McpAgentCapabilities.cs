using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpAgentCapabilities
{
	// ── Sampling: round-trip ───────────────────────────────────────────

	[TestMethod]
	[Description("When the client supports sampling, SampleAsync returns the LLM response text.")]
	public async Task When_ClientSupportsSampling_Then_SampleAsyncReturnsText()
	{
		var clientOptions = new McpClientOptions
		{
			Capabilities = new ClientCapabilities
			{
				Sampling = new SamplingCapability(),
			},
			Handlers = new McpClientHandlers
			{
				SamplingHandler = static (request, _, _) => ValueTask.FromResult(new CreateMessageResult
				{
					Content = [new TextContentBlock { Text = "name=0 email=2" }],
					Model = "test-model",
				}),
			},
		};

		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new SamplingModule()),
			configureOptions: null,
			clientOptions: clientOptions);

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		tools.Should().ContainSingle(t => string.Equals(t.Name, "sampling_test", StringComparison.Ordinal));

		var result = await fixture.Client.CallToolAsync(
			toolName: "sampling_test",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		var text = result.Content.OfType<TextContentBlock>().First().Text;
		text.Should().Contain("name=0 email=2");
	}

	// ── Sampling: not supported ───────────────────────────────────────

	[TestMethod]
	[Description("When the client does not support sampling, SampleAsync returns null.")]
	public async Task When_ClientDoesNotSupportSampling_Then_SampleAsyncReturnsNull()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new SamplingModule()));

		var result = await fixture.Client.CallToolAsync(
			toolName: "sampling_test",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		var text = result.Content.OfType<TextContentBlock>().First().Text;
		text.Should().Contain("not-supported");
	}

	// ── Sampling: parameter excluded from schema ──────────────────────

	[TestMethod]
	[Description("IMcpSampling parameter does not appear in the tool's JSON schema.")]
	public async Task When_ToolInjectsSampling_Then_SchemaHasNoSamplingParameter()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new SamplingModule()));

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(t => string.Equals(t.Name, "sampling_test", StringComparison.Ordinal));
		var schema = tool.JsonSchema.GetRawText();
		schema.Should().NotContain("sampling", because: "IMcpSampling is a framework-injected parameter and must not appear in the tool schema");
	}

	// ── Elicitation: text round-trip ──────────────────────────────────

	[TestMethod]
	[Description("ElicitTextAsync returns the user's text response.")]
	public async Task When_ClientSupportsElicitation_Then_ElicitTextAsyncReturnsText()
	{
		var clientOptions = CreateElicitationClientOptions((request, _) =>
		{
			return ValueTask.FromResult(new ElicitResult
			{
				Action = "accept",
				Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
				{
					["value"] = JsonSerializer.SerializeToElement("user-input-text"),
				},
			});
		});

		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new ElicitTextModule()),
			configureOptions: null,
			clientOptions: clientOptions);

		var result = await fixture.Client.CallToolAsync(
			toolName: "elicit_text_test",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		var text = result.Content.OfType<TextContentBlock>().First().Text;
		text.Should().Contain("user-input-text");
	}

	// ── Elicitation: choice round-trip ────────────────────────────────

	[TestMethod]
	[Description("ElicitChoiceAsync returns the zero-based index of the selected choice.")]
	public async Task When_ClientSupportsElicitation_Then_ElicitChoiceAsyncReturnsIndex()
	{
		var clientOptions = CreateElicitationClientOptions((request, _) =>
		{
			return ValueTask.FromResult(new ElicitResult
			{
				Action = "accept",
				Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
				{
					["value"] = JsonSerializer.SerializeToElement("overwrite"),
				},
			});
		});

		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new ElicitChoiceModule()),
			configureOptions: null,
			clientOptions: clientOptions);

		var result = await fixture.Client.CallToolAsync(
			toolName: "elicit_choice_test",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		var text = result.Content.OfType<TextContentBlock>().First().Text;
		text.Should().Contain("1"); // "overwrite" is at index 1 in ["skip", "overwrite", "keep-both"]
	}

	// ── Elicitation: boolean round-trip ───────────────────────────────

	[TestMethod]
	[Description("ElicitBooleanAsync returns the user's boolean response.")]
	public async Task When_ClientSupportsElicitation_Then_ElicitBooleanAsyncReturnsBool()
	{
		var clientOptions = CreateElicitationClientOptions((request, _) =>
		{
			return ValueTask.FromResult(new ElicitResult
			{
				Action = "accept",
				Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
				{
					["value"] = JsonSerializer.SerializeToElement(value: true),
				},
			});
		});

		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new ElicitBooleanModule()),
			configureOptions: null,
			clientOptions: clientOptions);

		var result = await fixture.Client.CallToolAsync(
			toolName: "elicit_boolean_test",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		var text = result.Content.OfType<TextContentBlock>().First().Text;
		text.Should().Contain("True");
	}

	// ── Elicitation: not supported ────────────────────────────────────

	[TestMethod]
	[Description("When the client does not support elicitation, all Elicit methods return null.")]
	public async Task When_ClientDoesNotSupportElicitation_Then_ElicitReturnsNull()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new ElicitTextModule()));

		var result = await fixture.Client.CallToolAsync(
			toolName: "elicit_text_test",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		var text = result.Content.OfType<TextContentBlock>().First().Text;
		text.Should().Contain("not-supported");
	}

	// ── Elicitation: user cancels ─────────────────────────────────────

	[TestMethod]
	[Description("When the user declines elicitation, the method returns null.")]
	public async Task When_UserCancelsElicitation_Then_ElicitReturnsNull()
	{
		var clientOptions = CreateElicitationClientOptions((_, _) =>
		{
			return ValueTask.FromResult(new ElicitResult { Action = "decline" });
		});

		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new ElicitTextModule()),
			configureOptions: null,
			clientOptions: clientOptions);

		var result = await fixture.Client.CallToolAsync(
			toolName: "elicit_text_test",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		var text = result.Content.OfType<TextContentBlock>().First().Text;
		text.Should().Contain("not-supported");
	}

	// ── Elicitation: parameter excluded from schema ───────────────────

	[TestMethod]
	[Description("IMcpElicitation parameter does not appear in the tool's JSON schema.")]
	public async Task When_ToolInjectsElicitation_Then_SchemaHasNoElicitationParameter()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new ElicitTextModule()));

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(t => string.Equals(t.Name, "elicit_text_test", StringComparison.Ordinal));
		var schema = tool.JsonSchema.GetRawText();
		schema.Should().NotContain("elicitation", because: "IMcpElicitation is a framework-injected parameter and must not appear in the tool schema");
	}

	[TestMethod]
	[Description("IMcpFeedback parameter does not appear in the tool's JSON schema.")]
	public async Task When_ToolInjectsFeedback_Then_SchemaHasNoFeedbackParameter()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new FeedbackModule()));

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(t => string.Equals(t.Name, "feedback_test", StringComparison.Ordinal));
		var schema = tool.JsonSchema.GetRawText();
		schema.Should().NotContain("feedback", because: "IMcpFeedback is a framework-injected parameter and must not appear in the tool schema");
	}

	// ── Helpers ───────────────────────────────────────────────────────

	private static McpClientOptions CreateElicitationClientOptions(
		Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler) =>
		new()
		{
			Capabilities = new ClientCapabilities
			{
				Elicitation = new ElicitationCapability(),
			},
			Handlers = new McpClientHandlers
			{
				ElicitationHandler = handler,
			},
		};

	// ── Test modules ──────────────────────────────────────────────────

	private sealed class SamplingModule : IReplModule
	{
		public void Map(IReplMap app)
		{
			app.Map(
				"sampling test",
				async (IMcpSampling sampling, CancellationToken ct) =>
				{
					var result = await sampling.SampleAsync("identify columns", cancellationToken: ct).ConfigureAwait(false);
					return result ?? "not-supported";
				}).ReadOnly();
		}
	}

	private sealed class ElicitTextModule : IReplModule
	{
		public void Map(IReplMap app)
		{
			app.Map(
				"elicit text test",
				async (IMcpElicitation elicitation, CancellationToken ct) =>
				{
					var result = await elicitation.ElicitTextAsync("Enter a value:", ct).ConfigureAwait(false);
					return result ?? "not-supported";
				}).ReadOnly();
		}
	}

	private sealed class ElicitChoiceModule : IReplModule
	{
		public void Map(IReplMap app)
		{
			app.Map(
				"elicit choice test",
				async (IMcpElicitation elicitation, CancellationToken ct) =>
				{
					var result = await elicitation.ElicitChoiceAsync(
						"How to handle duplicates?",
						["skip", "overwrite", "keep-both"],
						ct).ConfigureAwait(false);
					return result?.ToString(CultureInfo.InvariantCulture) ?? "not-supported";
				}).ReadOnly();
		}
	}

	private sealed class ElicitBooleanModule : IReplModule
	{
		public void Map(IReplMap app)
		{
			app.Map(
				"elicit boolean test",
				async (IMcpElicitation elicitation, CancellationToken ct) =>
				{
					var result = await elicitation.ElicitBooleanAsync("Confirm?", ct).ConfigureAwait(false);
					return result?.ToString() ?? "not-supported";
				}).ReadOnly();
		}
	}

	private sealed class FeedbackModule : IReplModule
	{
		public void Map(IReplMap app)
		{
			app.Map(
				"feedback test",
				(IMcpFeedback feedback) => feedback.IsLoggingSupported.ToString()).ReadOnly();
		}
	}
}
