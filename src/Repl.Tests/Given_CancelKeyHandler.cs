using AwesomeAssertions;
using System.Reflection;

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

			var method = typeof(CancelKeyHandler).GetMethod(
				"OnCancelKeyPress",
				BindingFlags.Instance | BindingFlags.NonPublic);
			method.Should().NotBeNull();
			var args = (ConsoleCancelEventArgs?)Activator.CreateInstance(
				typeof(ConsoleCancelEventArgs),
				BindingFlags.Instance | BindingFlags.NonPublic,
				binder: null,
				args: [ConsoleSpecialKey.ControlC],
				culture: null);
			args.Should().NotBeNull();
			method!.Invoke(handler, [null, args]);

			cts.IsCancellationRequested.Should().BeTrue();
			args!.Cancel.Should().BeTrue();
			sessionError.ToString().Should().Contain("Press Ctrl+C again to exit.");
			consoleError.ToString().Should().BeEmpty();
		}
		finally
		{
			Console.SetError(previousError);
		}
	}
}
