using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_CancelKeyHandler
{
	[TestMethod]
	[Description("CancelKeyHandler can be disposed safely multiple times.")]
	public void When_DisposedMultipleTimes_Then_NoException()
	{
		var handler = new CancelKeyHandler();
		handler.Dispose();
		handler.Dispose(); // Should not throw.
	}

	[TestMethod]
	[Description("CancelKeyHandler can be disposed without setting a command CTS.")]
	public void When_DisposedWithoutCommandCts_Then_NoException()
	{
		var handler = new CancelKeyHandler();
		handler.Dispose(); // Should not throw.
	}

	[TestMethod]
	[Description("SetCommandCts accepts null to clear the active command.")]
	public void When_CommandCtsSetToNull_Then_NoException()
	{
		using var handler = new CancelKeyHandler();
		using var cts = new CancellationTokenSource();
		handler.SetCommandCts(cts);
		handler.SetCommandCts(cts: null); // Should not throw.
	}
}
