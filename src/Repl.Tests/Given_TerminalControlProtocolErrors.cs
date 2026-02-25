namespace Repl.Tests;

[TestClass]
public sealed class Given_TerminalControlProtocolErrors
{
	[TestMethod]
	[Description("Regression guard: verifies malformed control payload is rejected so non-JSON noise does not mutate terminal metadata.")]
	public void When_PayloadIsMalformedJson_Then_TryParseReturnsFalse()
	{
		var raw = "@@repl:hello {\"terminal\":\"xterm-256color\",";

		var parsed = TerminalControlProtocol.TryParse(raw, out _);

		parsed.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies unknown control verb is ignored so unsupported extensions do not alter session metadata.")]
	public void When_VerbIsUnknown_Then_TryParseReturnsFalse()
	{
		var raw = "@@repl:ping {\"cols\":120,\"rows\":40}";

		var parsed = TerminalControlProtocol.TryParse(raw, out _);

		parsed.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies invalid capabilities string does not fail parsing and falls back to None.")]
	public void When_CapabilitiesStringIsInvalid_Then_MessageUsesNoneCapabilities()
	{
		var raw = "@@repl:hello {\"terminal\":\"xterm-256color\",\"capabilities\":\"Nope,Invalid\"}";

		var parsed = TerminalControlProtocol.TryParse(raw, out var message);

		parsed.Should().BeTrue();
		message.TerminalCapabilities.Should().Be(TerminalCapabilities.None);
	}

	[TestMethod]
	[Description("Regression guard: verifies invalid resize dimensions are ignored so window size updates stay valid-only.")]
	public void When_ResizePayloadHasInvalidDimensions_Then_WindowSizeIsNull()
	{
		var raw = "@@repl:resize {\"cols\":0,\"rows\":-1}";

		var parsed = TerminalControlProtocol.TryParse(raw, out var message);

		parsed.Should().BeTrue();
		message.WindowSize.Should().BeNull();
	}
}
