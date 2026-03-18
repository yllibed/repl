using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpInteractionChannel
{
	// ── AskChoiceAsync ─────────────────────────────────────────────────

	[TestMethod]
	[Description("Prefilled value resolves to matching choice index.")]
	public async Task When_PrefillMatchesChoice_Then_ReturnsIndex()
	{
		var channel = CreateChannel(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["color"] = "green" });

		var result = await channel.AskChoiceAsync("color", "Pick a color", ["red", "green", "blue"]);

		result.Should().Be(1);
	}

	[TestMethod]
	[Description("Prefilled numeric index resolves correctly.")]
	public async Task When_PrefillIsNumericIndex_Then_ReturnsIndex()
	{
		var channel = CreateChannel(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["color"] = "2" });

		var result = await channel.AskChoiceAsync("color", "Pick a color", ["red", "green", "blue"]);

		result.Should().Be(2);
	}

	[TestMethod]
	[Description("Missing prefill with default returns default.")]
	public async Task When_NoPrefillWithDefault_Then_ReturnsDefault()
	{
		var channel = CreateChannel();

		var result = await channel.AskChoiceAsync("color", "Pick a color", ["red", "green"], defaultIndex: 1);

		result.Should().Be(1);
	}

	[TestMethod]
	[Description("Missing prefill in PrefillThenDefaults mode returns 0.")]
	public async Task When_NoPrefillInDefaultsMode_Then_ReturnsZero()
	{
		var channel = CreateChannel(mode: InteractivityMode.PrefillThenDefaults);

		var result = await channel.AskChoiceAsync("color", "Pick a color", ["red", "green"]);

		result.Should().Be(0);
	}

	[TestMethod]
	[Description("Missing prefill in PrefillThenFail mode throws.")]
	public async Task When_NoPrefillInFailMode_Then_Throws()
	{
		var channel = CreateChannel(mode: InteractivityMode.PrefillThenFail);

		var act = () => channel.AskChoiceAsync("color", "Pick a color", ["red", "green"]).AsTask();

		await act.Should().ThrowAsync<McpInteractionException>();
	}

	// ── AskConfirmationAsync ───────────────────────────────────────────

	[TestMethod]
	[Description("Prefilled 'yes' returns true.")]
	public async Task When_PrefillIsYes_Then_ReturnsTrue()
	{
		var channel = CreateChannel(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["confirm"] = "yes" });

		var result = await channel.AskConfirmationAsync("confirm", "Proceed?");

		result.Should().BeTrue();
	}

	[TestMethod]
	[Description("Prefilled 'false' returns false.")]
	public async Task When_PrefillIsFalse_Then_ReturnsFalse()
	{
		var channel = CreateChannel(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["confirm"] = "false" });

		var result = await channel.AskConfirmationAsync("confirm", "Proceed?");

		result.Should().BeFalse();
	}

	// ── AskTextAsync ───────────────────────────────────────────────────

	[TestMethod]
	[Description("Prefilled value is returned.")]
	public async Task When_TextPrefill_Then_ReturnsValue()
	{
		var channel = CreateChannel(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["name"] = "Alice" });

		var result = await channel.AskTextAsync("name", "Enter name");

		result.Should().Be("Alice");
	}

	[TestMethod]
	[Description("Missing prefill with default returns default.")]
	public async Task When_NoTextPrefillWithDefault_Then_ReturnsDefault()
	{
		var channel = CreateChannel();

		var result = await channel.AskTextAsync("name", "Enter name", defaultValue: "Bob");

		result.Should().Be("Bob");
	}

	// ── AskSecretAsync ─────────────────────────────────────────────────

	[TestMethod]
	[Description("Secret with prefill returns value.")]
	public async Task When_SecretPrefill_Then_ReturnsValue()
	{
		var channel = CreateChannel(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["password"] = "s3cret" });

		var result = await channel.AskSecretAsync("password", "Enter password");

		result.Should().Be("s3cret");
	}

	[TestMethod]
	[Description("Secret without prefill always throws.")]
	public async Task When_NoSecretPrefill_Then_AlwaysThrows()
	{
		var channel = CreateChannel(mode: InteractivityMode.PrefillThenDefaults);

		var act = () => channel.AskSecretAsync("password", "Enter password").AsTask();

		await act.Should().ThrowAsync<McpInteractionException>();
	}

	// ── AskMultiChoiceAsync ────────────────────────────────────────────

	[TestMethod]
	[Description("Prefilled comma-separated values resolve to indices.")]
	public async Task When_MultiChoicePrefill_Then_ReturnsIndices()
	{
		var channel = CreateChannel(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tags"] = "red,blue" });

		var result = await channel.AskMultiChoiceAsync("tags", "Select tags", ["red", "green", "blue"]);

		result.Should().BeEquivalentTo([0, 2]);
	}

	// ── ClearScreenAsync ───────────────────────────────────────────────

	[TestMethod]
	[Description("ClearScreen is a no-op.")]
	public async Task When_ClearScreen_Then_NoOp()
	{
		var channel = CreateChannel();

		await channel.ClearScreenAsync(CancellationToken.None);
	}

	// ── DispatchAsync ──────────────────────────────────────────────────

	[TestMethod]
	[Description("DispatchAsync throws NotSupportedException.")]
	public void When_Dispatch_Then_ThrowsNotSupported()
	{
		var channel = CreateChannel();

		// DispatchAsync requires an InteractionRequest<T> subclass, but the throw
		// happens synchronously before any async work so we test the throw path.
		var act = () => channel.DispatchAsync<string>(null!, CancellationToken.None);

		act.Should().Throw<NotSupportedException>();
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	private static McpInteractionChannel CreateChannel(
		Dictionary<string, string>? prefills = null,
		InteractivityMode mode = InteractivityMode.PrefillThenFail) =>
		new(
			prefills ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			mode);
}
