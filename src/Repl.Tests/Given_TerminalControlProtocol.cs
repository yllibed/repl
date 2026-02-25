namespace Repl.Tests;

[TestClass]
public sealed class Given_TerminalControlProtocol
{
	[TestMethod]
	[Description("Regression guard: verifies hello control payload is parsed so websocket/signalr clients can publish terminal metadata.")]
	public void When_ParsingHelloMessage_Then_MetadataIsExtracted()
	{
		var raw = "@@repl:hello {\"terminal\":\"xterm-256color\",\"cols\":120,\"rows\":40,\"ansi\":true,\"capabilities\":\"Ansi,ResizeReporting\"}";

		var parsed = TerminalControlProtocol.TryParse(raw, out var message);

		parsed.Should().BeTrue();
		message.Kind.Should().Be(TerminalControlMessageKind.Hello);
		message.TerminalIdentity.Should().Be("xterm-256color");
		message.WindowSize.Should().Be((120, 40));
		message.AnsiSupported.Should().BeTrue();
		message.TerminalCapabilities.Should().Be(TerminalCapabilities.Ansi | TerminalCapabilities.ResizeReporting);
	}

	[TestMethod]
	[Description("Regression guard: verifies resize control payload is parsed so dynamic terminal resizing updates the active session.")]
	public void When_ParsingResizeMessage_Then_WindowSizeIsExtracted()
	{
		var raw = "@@repl:resize {\"cols\":132,\"rows\":43}";

		var parsed = TerminalControlProtocol.TryParse(raw, out var message);

		parsed.Should().BeTrue();
		message.Kind.Should().Be(TerminalControlMessageKind.Resize);
		message.WindowSize.Should().Be((132, 43));
	}
}
