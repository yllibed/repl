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
	/// <remarks>
	/// This is process-wide shared state. When multiple <see cref="ReplApp"/> instances
	/// coexist in the same process with different Spectre configurations, the last
	/// <see cref="SpectreReplExtensions.UseSpectreConsole"/> call wins. If per-app
	/// isolation is needed, this should be refactored to flow through the service
	/// container or <see cref="ReplSessionIO"/> instead.
	/// </remarks>
	internal static SpectreConsoleOptions Options { get; set; } = new();

	/// <summary>
	/// Diagnostic breadcrumb recording how the last console profile was derived
	/// (origin + gate verdict or legacy fallback). Read by tests when a CI-only
	/// environment produces a profile local runs cannot reproduce.
	/// </summary>
	internal static string? LastDetectionTrace { get; private set; }

	/// <summary>
	/// Creates a new <see cref="IAnsiConsole"/> bound to the current session I/O.
	/// </summary>
	public static IAnsiConsole Create(OutputOptions? outputOptions = null)
	{
		var settings = new AnsiConsoleSettings
		{
			Out = new SessionAnsiConsoleOutput(),
		};
		ApplyTerminalDetection(settings, outputOptions, origin: "session");

		return ApplyOptions(AnsiConsole.Create(settings));
	}

	/// <summary>
	/// Creates an <see cref="IAnsiConsole"/> that renders to the provided <see cref="TextWriter"/>.
	/// Used by the output transformer to capture rendered output as a string.
	/// </summary>
	public static IAnsiConsole CreateForWriter(TextWriter writer, int width, OutputOptions? outputOptions = null)
	{
		var settings = new AnsiConsoleSettings
		{
			Out = new WriterAnsiConsoleOutput(writer, width),
		};
		ApplyTerminalDetection(settings, outputOptions, origin: "writer");

		return ApplyOptions(AnsiConsole.Create(settings));
	}

	// Issue #46: the profile follows the host's terminal detection instead of hardcoding
	// ANSI + TrueColor. The gate is the same one driving shell-integration marks and
	// advanced progress: IsAnsiEnabled first, then the hosted capability fallback, with
	// the NO_COLOR > CLICOLOR_FORCE > TERM=dumb escape hatches honored. Callers pass
	// their app's OutputOptions explicitly (no process-wide static: parallel apps or
	// tests must not contaminate each other); when none is reachable (bare
	// AddSpectreConsole outside a Repl DI container), the legacy always-on behavior is
	// preserved so standalone consumers do not regress.
	private static void ApplyTerminalDetection(AnsiConsoleSettings settings, OutputOptions? outputOptions, string origin)
	{
		var effectiveOptions = outputOptions;
		if (effectiveOptions is null)
		{
			LastDetectionTrace = origin + ":legacy";
			settings.Ansi = AnsiSupport.Yes;
			settings.ColorSystem = ColorSystemSupport.TrueColor;
			return;
		}

		var ansiCapable = TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(effectiveOptions);
		LastDetectionTrace = origin + ":gate=" + (ansiCapable ? "true" : "false");
		settings.Ansi = ansiCapable ? AnsiSupport.Yes : AnsiSupport.No;
		settings.ColorSystem = ansiCapable ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors;
	}

	private static IAnsiConsole ApplyOptions(IAnsiConsole console)
	{
		// Unicode is gated on the FINAL sink's encoding, not the immediate writer: the
		// transformer renders into a UTF-16 StringWriter whose content is later written to
		// the session output, so the session writer (or Console.Out locally — the fallback
		// of ReplSessionIO.Output) is the encoding that actually has to carry the glyphs.
		console.Profile.Capabilities.Unicode =
			Options.Unicode && CanRenderBoxDrawing(ReplSessionIO.Output.Encoding);
		return console;
	}

	/// <summary>
	/// True when <paramref name="encoding"/> can carry Spectre's box-drawing glyphs.
	/// Trial-encodes a representative glyph and checks the roundtrip: a legacy codepage
	/// with a best-fit/replacement fallback turns it into '?', which is exactly the
	/// mojibake this guards against — cheaper and more truthful than a codepage allowlist.
	/// </summary>
	internal static bool CanRenderBoxDrawing(Encoding encoding)
	{
		const string probe = "╭";
		try
		{
			return string.Equals(encoding.GetString(encoding.GetBytes(probe)), probe, StringComparison.Ordinal);
		}
		catch (EncoderFallbackException)
		{
			return false;
		}
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
			if (ReplSessionIO.WindowSize is { } size && size.Width > 0)
			{
				return size.Width;
			}

			try
			{
				// Headless consoles (CI runners) can report 0 without throwing; a
				// zero-width profile makes Spectre render nothing, so fall back.
				var width = Console.WindowWidth;
				return width > 0 ? width : 120;
			}
			catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException)
			{
				return 120;
			}
		}

		private static int ResolveHeight()
		{
			if (ReplSessionIO.WindowSize is { } size && size.Height > 0)
			{
				return size.Height;
			}

			try
			{
				// Same headless-console guard as ResolveWidth.
				var height = Console.WindowHeight;
				return height > 0 ? height : 24;
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
