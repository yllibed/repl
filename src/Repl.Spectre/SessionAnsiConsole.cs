using System.IO;
using System.Text;

namespace Repl.Spectre;

/// <summary>
/// Creates <see cref="IAnsiConsole"/> instances that route output through
/// <see cref="ReplSessionIO.Output"/> for per-session isolation.
/// </summary>
internal static class SessionAnsiConsole
{
	/// <summary>
	/// Gets or sets the console options shared by all factory methods.
	/// Set by <see cref="SpectreReplExtensions.UseSpectreConsole"/> during app configuration.
	/// </summary>
	internal static SpectreConsoleOptions Options { get; set; } = new();

	/// <summary>
	/// Creates a new <see cref="IAnsiConsole"/> bound to the current session I/O.
	/// </summary>
	public static IAnsiConsole Create()
	{
		var settings = new AnsiConsoleSettings
		{
			Ansi = AnsiSupport.Yes,
			ColorSystem = ColorSystemSupport.TrueColor,
			Out = new SessionAnsiConsoleOutput(),
		};

		return ApplyOptions(AnsiConsole.Create(settings));
	}

	/// <summary>
	/// Creates an <see cref="IAnsiConsole"/> that renders to the provided <see cref="TextWriter"/>.
	/// Used by the output transformer to capture rendered output as a string.
	/// </summary>
	public static IAnsiConsole CreateForWriter(TextWriter writer, int width)
	{
		var settings = new AnsiConsoleSettings
		{
			Ansi = AnsiSupport.Yes,
			ColorSystem = ColorSystemSupport.TrueColor,
			Out = new WriterAnsiConsoleOutput(writer, width),
		};

		return ApplyOptions(AnsiConsole.Create(settings));
	}

	private static IAnsiConsole ApplyOptions(IAnsiConsole console)
	{
		console.Profile.Capabilities.Unicode = Options.Unicode;
		return console;
	}

	private sealed class SessionAnsiConsoleOutput : IAnsiConsoleOutput
	{
		public TextWriter Writer { get; } = new SessionDelegatingTextWriter();

		public bool IsTerminal => !ReplSessionIO.IsHostedSession && !Console.IsOutputRedirected;

		public int Width => ResolveWidth();

		public int Height => ResolveHeight();

		public void SetEncoding(Encoding encoding)
		{
			// Encoding is managed by the session writer.
		}

		private static int ResolveWidth()
		{
			if (ReplSessionIO.WindowSize is { } size)
			{
				return size.Width;
			}

			try
			{
				return Console.WindowWidth;
			}
			catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException)
			{
				return 120;
			}
		}

		private static int ResolveHeight()
		{
			if (ReplSessionIO.WindowSize is { } size)
			{
				return size.Height;
			}

			try
			{
				return Console.WindowHeight;
			}
			catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException)
			{
				return 24;
			}
		}
	}

	private sealed class WriterAnsiConsoleOutput(TextWriter writer, int width) : IAnsiConsoleOutput
	{
		public TextWriter Writer => writer;

		public bool IsTerminal => false;

		public int Width => width;

		public int Height => 24;

		public void SetEncoding(Encoding encoding)
		{
		}
	}

	/// <summary>
	/// TextWriter that delegates all writes to <see cref="ReplSessionIO.Output"/>
	/// at call time, ensuring session-correct routing even if captured early.
	/// </summary>
	private sealed class SessionDelegatingTextWriter : TextWriter
	{
		public override Encoding Encoding => ReplSessionIO.Output.Encoding;

		public override void Write(char value) => ReplSessionIO.Output.Write(value);

		public override void Write(string? value) => ReplSessionIO.Output.Write(value);

		public override void Write(char[] buffer, int index, int count) =>
			ReplSessionIO.Output.Write(buffer, index, count);

		public override void WriteLine() => ReplSessionIO.Output.WriteLine();

		public override void WriteLine(string? value) => ReplSessionIO.Output.WriteLine(value);

		public override void Flush() => ReplSessionIO.Output.Flush();

		public override Task WriteAsync(char value) => ReplSessionIO.Output.WriteAsync(value);

		public override Task WriteAsync(string? value) => ReplSessionIO.Output.WriteAsync(value);

		public override Task WriteLineAsync() => ReplSessionIO.Output.WriteLineAsync();

		public override Task WriteLineAsync(string? value) => ReplSessionIO.Output.WriteLineAsync(value);

		public override Task FlushAsync() => ReplSessionIO.Output.FlushAsync();
	}
}
