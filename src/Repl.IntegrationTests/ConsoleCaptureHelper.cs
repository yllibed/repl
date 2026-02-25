namespace Repl.IntegrationTests;

internal static class ConsoleCaptureHelper
{
	public static (int ExitCode, string Text) Capture(Func<int> action)
	{
		ArgumentNullException.ThrowIfNull(action);

		var previous = Console.Out;
		using var writer = new StringWriter();
		Console.SetOut(writer);

		try
		{
			var exitCode = action();
			return (exitCode, writer.ToString());
		}
		finally
		{
			Console.SetOut(previous);
		}
	}

	public static (int ExitCode, string Text) CaptureWithInput(string input, Func<int> action)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(action);

		var previousOut = Console.Out;
		var previousIn = Console.In;
		using var writer = new StringWriter();
		using var reader = new StringReader(input);
		Console.SetOut(writer);
		Console.SetIn(reader);

		try
		{
			var exitCode = action();
			return (exitCode, writer.ToString());
		}
		finally
		{
			Console.SetOut(previousOut);
			Console.SetIn(previousIn);
		}
	}

	public static async Task<(int ExitCode, string Text)> CaptureAsync(Func<Task<int>> action)
	{
		ArgumentNullException.ThrowIfNull(action);

		var previous = Console.Out;
		using var writer = new StringWriter();
		Console.SetOut(writer);

		try
		{
			var exitCode = await action().ConfigureAwait(false);
			return (exitCode, writer.ToString());
		}
		finally
		{
			Console.SetOut(previous);
		}
	}
}
