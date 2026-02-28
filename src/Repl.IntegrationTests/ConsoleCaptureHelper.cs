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

	public static (int ExitCode, string StdOut, string StdErr) CaptureStdOutAndErr(Func<int> action)
	{
		ArgumentNullException.ThrowIfNull(action);

		var previousOut = Console.Out;
		var previousErr = Console.Error;
		using var stdout = new StringWriter();
		using var stderr = new StringWriter();
		Console.SetOut(stdout);
		Console.SetError(stderr);

		try
		{
			var exitCode = action();
			return (exitCode, stdout.ToString(), stderr.ToString());
		}
		finally
		{
			Console.SetOut(previousOut);
			Console.SetError(previousErr);
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

	public static (int ExitCode, string StdOut, string StdErr) CaptureWithInputStdOutAndErr(
		string input,
		Func<int> action)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(action);

		var previousOut = Console.Out;
		var previousErr = Console.Error;
		var previousIn = Console.In;
		using var stdout = new StringWriter();
		using var stderr = new StringWriter();
		using var reader = new StringReader(input);
		Console.SetOut(stdout);
		Console.SetError(stderr);
		Console.SetIn(reader);

		try
		{
			var exitCode = action();
			return (exitCode, stdout.ToString(), stderr.ToString());
		}
		finally
		{
			Console.SetOut(previousOut);
			Console.SetError(previousErr);
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
