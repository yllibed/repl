namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_TerminalMetadataLifecycle
{
	[TestMethod]
	[Description("Regression guard: verifies run options overrides win at startup so host baseline values cannot override explicit metadata.")]
	public async Task When_RunningWithTerminalOverrides_Then_SessionInfoReflectsOverrides()
	{
		var sut = CreateSut();
		var host = new StreamedReplHost(new StringWriter(), new StaticWindowSizeProvider((77, 21)))
		{
			TransportName = "websocket",
			RemotePeer = "198.51.100.10:4000",
			TerminalIdentityResolver = () => "resolver-term",
		};

		var options = new ReplRunOptions
		{
			TerminalOverrides = new TerminalSessionOverrides
			{
				TransportName = "override-transport",
				RemotePeer = "override-remote",
				TerminalIdentity = "override-terminal",
				WindowSize = (132, 43),
				AnsiSupported = false,
				TerminalCapabilities = TerminalCapabilities.ResizeReporting | TerminalCapabilities.IdentityReporting,
			},
		};

		host.EnqueueInput($"exit{Environment.NewLine}");
		var exitCode = await host.RunSessionAsync(sut, options);

		exitCode.Should().Be(0);
		ReplSessionIO.TryGetSession(host.SessionId, out var session).Should().BeTrue();
		session.TransportName.Should().Be("override-transport");
		session.RemotePeer.Should().Be("override-remote");
		session.TerminalIdentity.Should().Be("override-terminal");
		session.WindowSize.Should().Be((132, 43));
		session.AnsiSupport.Should().BeFalse();
		session.TerminalCapabilities.Should().HaveFlag(TerminalCapabilities.ResizeReporting);
		session.TerminalCapabilities.Should().HaveFlag(TerminalCapabilities.IdentityReporting);

		await host.DisposeAsync();
	}

	[TestMethod]
	[Description("Regression guard: verifies terminal identity resolver populates startup metadata when no explicit identity override is provided.")]
	public async Task When_TerminalIdentityComesFromResolver_Then_MetadataIncludesResolvedIdentity()
	{
		var sut = CreateSut();
		var host = new StreamedReplHost(new StringWriter(), new StaticWindowSizeProvider((100, 30)))
		{
			TransportName = "signalr",
			RemotePeer = "203.0.113.7:2222",
			TerminalIdentityResolver = () => "xterm-256color",
		};

		host.EnqueueInput($"exit{Environment.NewLine}");
		var exitCode = await host.RunSessionAsync(sut, new ReplRunOptions());

		exitCode.Should().Be(0);
		ReplSessionIO.TryGetSession(host.SessionId, out var session).Should().BeTrue();
		session.TerminalIdentity.Should().Be("xterm-256color");
		session.WindowSize.Should().Be((100, 30));
		session.TransportName.Should().Be("signalr");
		session.RemotePeer.Should().Be("203.0.113.7:2222");

		await host.DisposeAsync();
	}

	[TestMethod]
	[Description("Regression guard: verifies runtime metadata updates apply deterministically so latest size wins while capability flags are merged.")]
	public async Task When_RuntimeUpdatesArrive_Then_WindowSizeUsesLastValueAndCapabilitiesAreMerged()
	{
		var sut = CreateSut();
		var host = new StreamedReplHost(new StringWriter(), new StaticWindowSizeProvider());

		host.UpdateWindowSize(80, 24);
		host.UpdateWindowSize(120, 40);
		host.ApplyControlMessage(
			new TerminalControlMessage(
				TerminalControlMessageKind.Hello,
				TerminalIdentity: "xterm-256color",
				AnsiSupported: true,
				TerminalCapabilities: TerminalCapabilities.VtInput));

		host.EnqueueInput($"exit{Environment.NewLine}");
		var exitCode = await host.RunSessionAsync(sut, new ReplRunOptions());

		exitCode.Should().Be(0);
		ReplSessionIO.TryGetSession(host.SessionId, out var session).Should().BeTrue();
		session.WindowSize.Should().Be((120, 40));
		session.AnsiSupport.Should().BeTrue();
		session.TerminalCapabilities.Should().HaveFlag(TerminalCapabilities.ResizeReporting);
		session.TerminalCapabilities.Should().HaveFlag(TerminalCapabilities.VtInput);
		session.TerminalCapabilities.Should().HaveFlag(TerminalCapabilities.Ansi);
		session.TerminalIdentity.Should().Be("xterm-256color");

		await host.DisposeAsync();
	}

	[TestMethod]
	[Description("Regression guard: verifies session metadata is removed when streamed host is disposed so no stale session records remain.")]
	public async Task When_StreamedHostIsDisposed_Then_SessionMetadataIsRemoved()
	{
		var sut = CreateSut();
		var host = new StreamedReplHost(new StringWriter(), new StaticWindowSizeProvider((90, 28)));
		var sessionId = host.SessionId;

		ReplSessionIO.TryGetSession(sessionId, out _).Should().BeTrue();

		host.EnqueueInput($"exit{Environment.NewLine}");
		var exitCode = await host.RunSessionAsync(sut, new ReplRunOptions());

		exitCode.Should().Be(0);
		ReplSessionIO.TryGetSession(sessionId, out _).Should().BeTrue();

		await host.DisposeAsync();

		ReplSessionIO.TryGetSession(sessionId, out _).Should().BeFalse();
	}

	private static ReplApp CreateSut()
	{
		return ReplApp.Create().UseDefaultInteractive();
	}

	private sealed class StaticWindowSizeProvider((int Width, int Height)? initialSize = null) : IWindowSizeProvider
	{
		private readonly (int Width, int Height)? _initialSize = initialSize;

		public event EventHandler<WindowSizeEventArgs>? SizeChanged;

		public ValueTask<(int Width, int Height)?> GetSizeAsync(CancellationToken cancellationToken) =>
			ValueTask.FromResult(_initialSize);

		public void Push(int width, int height) =>
			SizeChanged?.Invoke(this, new WindowSizeEventArgs(width, height));
	}
}
