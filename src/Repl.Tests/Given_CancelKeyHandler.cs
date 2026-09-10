using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
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

	[TestMethod]
	[Description("Ctrl+Break is routed to the active interactive command on Windows.")]
	public void When_CtrlBreakArrivesOnWindowsDuringCommand_Then_CommandIsCancelled()
	{
		using var handler = new CancelKeyHandler();
		using var cancellation = new CancellationTokenSource();
		handler.SetCommandCts(cancellation);

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting(
			ConsoleSpecialKey.ControlBreak,
			isWindows: true);

		result.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		cancellation.IsCancellationRequested.Should().BeTrue();
	}

	[TestMethod]
	[Description("ControlBreak represents SIGQUIT on Unix and is left to the operating system.")]
	public void When_ControlBreakArrivesOnUnixDuringCommand_Then_SigQuitIsNotClaimed()
	{
		using var handler = new CancelKeyHandler();
		using var cancellation = new CancellationTokenSource();
		handler.SetCommandCts(cancellation);

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting(
			ConsoleSpecialKey.ControlBreak,
			isWindows: false);

		result.Should().Be(ConsoleCancelKeyHandlingResult.NotHandled);
		cancellation.IsCancellationRequested.Should().BeFalse();
	}

	[TestMethod]
	[Description("First Ctrl+C writes the double-tap hint to ReplSessionIO.Error so protocol/session error routing remains consistent.")]
	public void When_FirstCancelPressDuringCommand_Then_HintUsesSessionErrorWriter()
	{
		var previousError = Console.Error;
		using var consoleError = new StringWriter();
		Console.SetError(consoleError);
		try
		{
			using var sessionOutput = new StringWriter();
			using var sessionError = new StringWriter();
			using var sessionScope = ReplSessionIO.SetSession(
				sessionOutput,
				TextReader.Null,
				error: sessionError,
				commandOutput: sessionOutput,
				isHostedSession: false);
			using var handler = new CancelKeyHandler();
			using var cts = new CancellationTokenSource();
			handler.SetCommandCts(cts);

			var result = handler.HandleCancelKeyForTesting();

			cts.IsCancellationRequested.Should().BeTrue();
			result.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
			sessionError.ToString().Should().Contain("Press Ctrl+C again to exit.");
			consoleError.ToString().Should().BeEmpty();
		}
		finally
		{
			Console.SetError(previousError);
		}
	}
}
