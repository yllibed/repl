namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_CustomAmbientCommands
{
	[TestMethod]
	[Description("Verifies a custom ambient command is dispatched in interactive mode.")]
	public void When_CustomAmbientCommandIsTyped_Then_HandlerIsExecuted()
	{
		var executed = false;
		var sut = ReplApp.Create()
			.UseDefaultInteractive()
			.Options(o => o.AmbientCommands.MapAmbient(
				"ping",
				() => { executed = true; },
				"Test ambient command"));
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"ping\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		executed.Should().BeTrue();
	}

	[TestMethod]
	[Description("Verifies a custom ambient command receives injected services.")]
	public void When_CustomAmbientCommandUsesInjection_Then_ServicesAreProvided()
	{
		string? captured = null;
		var sut = ReplApp.Create()
			.UseDefaultInteractive()
			.Options(o => o.AmbientCommands.MapAmbient(
				"greet",
				(CancellationToken ct) => { captured = "injected"; }));
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"greet\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		captured.Should().Be("injected");
	}

	[TestMethod]
	[Description("Verifies custom ambient commands appear in help output.")]
	public void When_HelpIsShown_Then_CustomAmbientCommandAppearsInGlobalCommands()
	{
		var sut = ReplApp.Create()
			.UseDefaultInteractive()
			.Options(o => o.AmbientCommands.MapAmbient(
				"ping",
				() => { },
				"Send a ping"));
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"help\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ping");
		output.Text.Should().Contain("Send a ping");
	}

	[TestMethod]
	[Description("Verifies custom ambient commands work inside a nested scope.")]
	public void When_CustomAmbientCommandIsTypedInScope_Then_HandlerIsExecuted()
	{
		var executed = false;
		var sut = ReplApp.Create()
			.UseDefaultInteractive()
			.Options(o => o.AmbientCommands.MapAmbient(
				"ping",
				() => { executed = true; }));
		sut.Context("sub", (IReplMap sub) =>
		{
			sub.Map("foo", () => "bar");
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"sub\nping\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		executed.Should().BeTrue();
	}

	[TestMethod]
	[Description("Verifies ClearScreenAsync can be called from a custom ambient command.")]
	public void When_ClearScreenAmbientCommand_Then_NoError()
	{
		var sut = ReplApp.Create()
			.UseDefaultInteractive()
			.Options(o => o.AmbientCommands.MapAmbient(
				"clear",
				[System.ComponentModel.Description("Clear the screen")]
				async (IReplInteractionChannel channel, CancellationToken ct) =>
				{
					await channel.ClearScreenAsync(ct).ConfigureAwait(false);
				}));
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"clear\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("Verifies custom ambient command takes precedence over unknown command error.")]
	public void When_CustomAmbientCommandIsRegistered_Then_NoUnknownCommandError()
	{
		var sut = ReplApp.Create()
			.UseDefaultInteractive()
			.Options(o => o.AmbientCommands.MapAmbient(
				"custom",
				() => { }));
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"custom\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain("Unknown command");
	}

	[TestMethod]
	[Description("Verifies async custom ambient commands are awaited properly.")]
	public void When_AsyncCustomAmbientCommand_Then_HandlerIsAwaited()
	{
		var executed = false;
		var sut = ReplApp.Create()
			.UseDefaultInteractive()
			.Options(o => o.AmbientCommands.MapAmbient(
				"async-ping",
				async (CancellationToken ct) =>
				{
					await Task.Yield();
					executed = true;
				}));
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"async-ping\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		executed.Should().BeTrue();
	}
}
