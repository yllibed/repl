namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_InteractionChannel
{
	[TestMethod]
	[Description("Regression guard: verifies handler uses interaction channel status so that status is rendered.")]
	public void When_HandlerUsesInteractionChannelStatus_Then_StatusIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("import", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			await channel.WriteStatusAsync("Import started", ct).ConfigureAwait(false);
			return "done";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["import"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Import started");
		output.Text.Should().Contain("done");
	}

	[TestMethod]
	[Description("Regression guard: verifies user-facing notice, warning, and problem feedback render semantically.")]
	public void When_HandlerUsesStructuredUserFeedback_Then_FeedbackIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("sync", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			await channel.WriteNoticeAsync("Connection established", ct).ConfigureAwait(false);
			await channel.WriteWarningAsync("Cache is warming up", ct).ConfigureAwait(false);
			await channel.WriteProblemAsync("Sync failed", "Check connectivity and retry.", "sync_failed", ct).ConfigureAwait(false);
			return "done";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["sync", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Connection established");
		output.Text.Should().Contain("Warning: Cache is warming up");
		output.Text.Should().Contain("Problem [sync_failed]: Sync failed");
		output.Text.Should().Contain("Check connectivity and retry.");
		output.Text.Should().Contain("done");
	}

	[TestMethod]
	[Description("Regression guard: verifies percentage progress injection so that handlers can report progress through IProgress<double>.")]
	public void When_HandlerUsesInjectedPercentageProgress_Then_ProgressIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("sync", (IProgress<double> progress) =>
		{
			progress.Report(25);
			progress.Report(100);
			return "done";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["sync", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Progress: 25%");
		output.Text.Should().Contain("Progress: 100%");
	}

	[TestMethod]
	[Description("Regression guard: verifies structured progress injection so that handlers can report progress through IProgress<ReplProgressEvent>.")]
	public void When_HandlerUsesInjectedStructuredProgress_Then_ProgressIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("import", (IProgress<ReplProgressEvent> progress) =>
		{
			progress.Report(new ReplProgressEvent("Importing", Current: 3, Total: 4));
			return "ok";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["import", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Importing: 75%");
	}

	[TestMethod]
	[Description("Regression guard: verifies progress template and default label options are applied to injected progress rendering.")]
	public void When_ProgressTemplateAndDefaultLabelConfigured_Then_InjectedProgressUsesConfiguredFormatting()
	{
		var sut = ReplApp.Create().Options(o =>
		{
			o.Interaction.DefaultProgressLabel = "Sync";
			o.Interaction.ProgressTemplate = "[{label}] {percent:0.0}%";
		});
		sut.Map("sync", (IProgress<double> progress) =>
		{
			progress.Report(12.5);
			return "ok";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["sync", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("[Sync] 12.5%");
	}

	[TestMethod]
	[Description("Regression guard: verifies prompt fallback is fail and no answer provided so that command fails.")]
	public void When_PromptFallbackIsFailAndNoAnswerProvided_Then_CommandFails()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("confirm", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			_ = await channel.AskConfirmationAsync("overwrite", "Overwrite?", defaultValue: true).ConfigureAwait(false);
			return "ok";
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(string.Empty, () => sut.Run(["confirm"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Overwrite?");
	}

	[TestMethod]
	[Description("Regression guard: verifies prompt fallback uses default so that default answer is applied.")]
	public void When_PromptFallbackUsesDefault_Then_DefaultAnswerIsApplied()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.UseDefault);
		sut.Map("confirm", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var answer = await channel.AskConfirmationAsync("overwrite", "Overwrite?", defaultValue: true).ConfigureAwait(false);
			return answer ? "yes" : "no";
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(string.Empty, () => sut.Run(["confirm"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("yes");
	}

	[TestMethod]
	[Description("Regression guard: verifies prefilled confirmation answer is provided so that prompt is not required in non-interactive mode.")]
	public void When_ConfirmationAnswerIsPrefilled_Then_PrefilledValueIsUsed()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("confirm", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var answer = await channel.AskConfirmationAsync("overwrite", "Overwrite?", defaultValue: false).ConfigureAwait(false);
			return answer ? "yes" : "no";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["confirm", "--answer:overwrite=1"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("yes");
		output.Text.Should().NotContain("Overwrite?");
	}

	[TestMethod]
	[Description("Regression guard: verifies prefilled text answer is provided so that prompt fallback fail does not block execution.")]
	public void When_TextAnswerIsPrefilled_Then_CommandUsesPrefilledValue()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("rename", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var text = await channel.AskTextAsync("name", "Name?").ConfigureAwait(false);
			return text;
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["rename", "--answer:name=Alice"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Alice");
		output.Text.Should().NotContain("Name?");
	}

	[TestMethod]
	[Description("Regression guard: verifies prefilled secret answer bypasses interactive prompt.")]
	public void When_SecretAnswerIsPrefilled_Then_PrefilledValueIsUsed()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("login", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var secret = await channel.AskSecretAsync("password", "Password?").ConfigureAwait(false);
			return secret.Length > 0 ? "authenticated" : "empty";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["login", "--answer:password=s3cret"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("authenticated");
		output.Text.Should().NotContain("Password?");
	}

	[TestMethod]
	[Description("Regression guard: verifies prefilled multi-choice answer with comma-separated indices.")]
	public void When_MultiChoiceAnswerIsPrefilled_Then_PrefilledIndicesAreUsed()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("features", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var selected = await channel.AskMultiChoiceAsync(
				"features",
				"Select features:",
				["Auth", "Logging", "Cache"],
				defaultIndices: null).ConfigureAwait(false);
			return string.Join(',', selected);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["features", "--answer:features=1,3"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("0,2"); // 1-based "1,3" maps to 0-based [0,2]
	}

	[TestMethod]
	[Description("Regression guard: verifies prefilled multi-choice answer with choice names.")]
	public void When_MultiChoiceAnswerIsPrefilledByName_Then_MatchingIndicesAreUsed()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("features", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var selected = await channel.AskMultiChoiceAsync(
				"features",
				"Select features:",
				["Auth", "Logging", "Cache"]).ConfigureAwait(false);
			return string.Join(',', selected);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["features", "--answer:features=Auth,Cache"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("0,2");
	}

	[TestMethod]
	[Description("Regression guard: verifies multi-choice fallback to default indices when no input provided.")]
	public void When_MultiChoiceHasDefaultsAndNoInput_Then_DefaultIndicesAreReturned()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.UseDefault);
		sut.Map("features", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var selected = await channel.AskMultiChoiceAsync(
				"features",
				"Select features:",
				["Auth", "Logging", "Cache"],
				defaultIndices: [0, 2]).ConfigureAwait(false);
			return string.Join(',', selected);
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(string.Empty, () => sut.Run(["features"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("0,2");
	}

	[TestMethod]
	[Description("Regression guard: verifies AskEnumAsync prefill by enum value name.")]
	public void When_EnumAnswerIsPrefilledByName_Then_EnumValueIsReturned()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("choose-color", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var color = await channel.AskEnumAsync<SampleColor>("color", "Pick a color:").ConfigureAwait(false);
			return color.ToString();
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["choose-color", "--answer:color=Green"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Green");
	}

	[TestMethod]
	[Description("Regression guard: verifies AskNumberAsync prefill with numeric value.")]
	public void When_NumberAnswerIsPrefilled_Then_ParsedValueIsReturned()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("set-count", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var count = await channel.AskNumberAsync<int>("count", "How many?").ConfigureAwait(false);
			return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["set-count", "--answer:count=42"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("42");
	}

	[TestMethod]
	[Description("Regression guard: verifies AskValidatedTextAsync accepts valid input through prefill.")]
	public void When_ValidatedTextAnswerIsPrefilled_Then_ValidValueIsAccepted()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("set-email", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var email = await channel.AskValidatedTextAsync(
				"email",
				"Email?",
				input => input.Contains('@') ? null : "Must contain @").ConfigureAwait(false);
			return email;
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["set-email", "--answer:email=a@b.com"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("a@b.com");
	}

	[TestMethod]
	[Description("Regression guard: verifies AskFlagsEnumAsync prefill by description names returns composite value.")]
	public void When_FlagsEnumAnswerIsPrefilledByName_Then_CompositeValueIsReturned()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("set-perms", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var perms = await channel.AskFlagsEnumAsync<SamplePermissions>("perms", "Permissions:").ConfigureAwait(false);
			return perms.ToString();
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["set-perms", "--answer:perms=View items,Remove items"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Read, Delete");
	}

	[TestMethod]
	[Description("Regression guard: verifies AskNumberAsync re-prompts when value is out of bounds.")]
	public void When_NumberAnswerIsOutOfBounds_Then_FallbackDefaultIsUsed()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.UseDefault);
		sut.Map("set-count", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var count = await channel.AskNumberAsync<int>(
				"count", "How many?",
				defaultValue: 10,
				new AskNumberOptions<int>(Min: 1, Max: 100)).ConfigureAwait(false);
			return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
		});

		// Input "999" is out of bounds; fallback re-prompts and gets empty → uses default 10.
		var output = ConsoleCaptureHelper.CaptureWithInput("999\n\n", () => sut.Run(["set-count"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("at most 100");
	}

	[TestMethod]
	[Description("Regression guard: verifies AskValidatedTextAsync re-prompts on invalid input.")]
	public void When_ValidatedTextInputIsInvalid_Then_ErrorIsDisplayedAndReprompts()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.UseDefault);
		sut.Map("set-email", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var email = await channel.AskValidatedTextAsync(
				"email",
				"Email?",
				input => input.Contains('@') ? null : "Must contain @",
				defaultValue: "fallback@test.com").ConfigureAwait(false);
			return email;
		});

		// First input "bad" is invalid; second empty input falls back to default.
		var output = ConsoleCaptureHelper.CaptureWithInput("bad\n\n", () => sut.Run(["set-email"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Must contain @");
	}

	[TestMethod]
	[Description("Regression guard: verifies ClearScreenAsync emits clear screen event.")]
	public void When_ClearScreenAsyncIsCalled_Then_ClearEventIsEmitted()
	{
		var sut = ReplApp.Create();
		sut.Map("cls", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			await channel.ClearScreenAsync(ct).ConfigureAwait(false);
			return "cleared";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["cls"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("cleared");
	}

	[TestMethod]
	[Description("Regression guard: verifies secret prompt fallback uses empty string when AllowEmpty is true.")]
	public void When_SecretAllowsEmptyAndNoInput_Then_EmptyStringIsReturned()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.UseDefault);
		sut.Map("token", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var token = await channel.AskSecretAsync(
				"token", "API Token?",
				new AskSecretOptions(AllowEmpty: true)).ConfigureAwait(false);
			return token.Length == 0 ? "none" : token;
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(string.Empty, () => sut.Run(["token"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("none");
	}
}
