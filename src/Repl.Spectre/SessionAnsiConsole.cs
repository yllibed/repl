using System.IO;
using System.Text;

namespace Repl.Spectre;

/// <summary>
/// Creates <see cref="IAnsiConsole"/> instances that route output through
/// <see cref="ReplSessionIO.Output"/> for per-session isolation.
/// </summary>
internal static class SessionAnsiConsole
{
	// Defaults used when no per-app options are reachable (bare AddSpectreConsole
	// outside a Repl container). Never mutated.
	private static readonly SpectreConsoleOptions s_defaultOptions = new();

	/// <summary>
	/// Creates a new <see cref="IAnsiConsole"/> bound to the current session I/O.
	/// </summary>
	public static IAnsiConsole Create(OutputOptions? outputOptions = null, SpectreConsoleOptions? spectreOptions = null)
	{
		var settings = new AnsiConsoleSettings
		{
			Out = new SessionAnsiConsoleOutput(),
			// Spectre's default profile enrichers (GitHub Actions, GitLab, TeamCity, …)
			// force ANSI back on under CI, overriding the explicit Ansi/ColorSystem below;
			// the host detection is authoritative here, so enrichment is disabled. That
			// also loses the enrichers' Interactive=false under CI, so interactivity is
			// derived explicitly: prompts can only be answered on a local, non-redirected
			// stdin (mirrors SpectreInteractionHandler.CanHandle).
			Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
			Interactive = !ReplSessionIO.IsHostedSession && !Console.IsInputRedirected
				? InteractionSupport.Yes
				: InteractionSupport.No,
		};
		ApplyTerminalDetection(settings, outputOptions);

		return ApplyOptions(AnsiConsole.Create(settings), spectreOptions);
	}

	/// <summary>
	/// Creates an <see cref="IAnsiConsole"/> that renders to the provided <see cref="TextWriter"/>.
	/// Used by the output transformer to capture rendered output as a string.
	/// </summary>
	public static IAnsiConsole CreateForWriter(TextWriter writer, int width, OutputOptions? outputOptions = null, SpectreConsoleOptions? spectreOptions = null)
	{
		var settings = new AnsiConsoleSettings
		{
			Out = new WriterAnsiConsoleOutput(writer, width),
			// Same rationale as Create: CI enrichers must not override the host detection.
			// A capture writer can never answer prompts.
			Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
			Interactive = InteractionSupport.No,
		};
		ApplyTerminalDetection(settings, outputOptions);

		return ApplyOptions(AnsiConsole.Create(settings), spectreOptions);
	}

	// Issue #46: the profile follows the host's terminal detection instead of hardcoding
	// ANSI + TrueColor. The gate is the same one driving shell-integration marks and
	// advanced progress: IsAnsiEnabled first, then the hosted capability fallback, with
	// the NO_COLOR > CLICOLOR_FORCE > TERM=dumb escape hatches honored. Callers pass
	// their app's OutputOptions explicitly (no process-wide static: parallel apps or
	// tests must not contaminate each other); when none is reachable (bare
	// AddSpectreConsole outside a Repl DI container), the decision is handed to
	// Spectre's own detection rather than forcing always-on output.
	private static void ApplyTerminalDetection(AnsiConsoleSettings settings, OutputOptions? outputOptions)
	{
		if (outputOptions is null)
		{
			settings.Ansi = AnsiSupport.Detect;
			settings.ColorSystem = ColorSystemSupport.Detect;
			return;
		}

		var ansiCapable = TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(outputOptions);
		settings.Ansi = ansiCapable ? AnsiSupport.Yes : AnsiSupport.No;
		settings.ColorSystem = ansiCapable ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors;
	}

	private static IAnsiConsole ApplyOptions(IAnsiConsole console, SpectreConsoleOptions? spectreOptions)
	{
		// Unicode is gated on the FINAL sink's encoding, not the immediate writer: the
		// transformer renders into a UTF-16 StringWriter whose content is later written to
		// the session output, so the session writer (or Console.Out locally — the fallback
		// of ReplSessionIO.Output) is the encoding that actually has to carry the glyphs.
		console.Profile.Capabilities.Unicode =
			(spectreOptions ?? s_defaultOptions).Unicode && CanRenderBoxDrawing(ReplSessionIO.Output.Encoding);
		return console;
	}

	// Single-entry cache: the verdict is constant per Encoding instance (they are
	// effectively singletons), and consoles are created per render/prompt.
	private static (Encoding Encoding, bool Verdict)? s_boxDrawingVerdict;

	/// <summary>
	/// True when <paramref name="encoding"/> can carry Spectre's box-drawing glyphs.
	/// Trial-encodes a representative glyph and checks the roundtrip: a legacy codepage
	/// with a best-fit/replacement fallback turns it into '?', which is exactly the
	/// mojibake this guards against — cheaper and more truthful than a codepage allowlist.
	/// </summary>
	internal static bool CanRenderBoxDrawing(Encoding encoding)
	{
		if (s_boxDrawingVerdict is { } cached && ReferenceEquals(cached.Encoding, encoding))
		{
			return cached.Verdict;
		}

		var verdict = ProbeBoxDrawing(encoding);
		s_boxDrawingVerdict = (encoding, verdict);
		return verdict;
	}

	private static bool ProbeBoxDrawing(Encoding encoding)
	{
		const string probe = "╭";
		try
		{
			return string.Equals(encoding.GetString(encoding.GetBytes(probe)), probe, StringComparison.Ordinal);
		}
		catch (Exception)
		{
			// Hosted transports can declare arbitrary TextWriter.Encoding implementations;
			// anything the probe cannot roundtrip — including by throwing — conservatively
			// means no box drawing rather than crashing every render of the session.
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

		private const int FallbackWidth = 120;
		private const int FallbackHeight = 24;

		// Client-advertised sizes are untrusted input: a hosted client reporting
		// int.MaxValue would otherwise drive Spectre into proportional allocations.
		private const int MaxWidth = 10_000;
		private const int MaxHeight = 1_000;

		private static int ResolveWidth()
		{
			if (ReplSessionIO.WindowSize is { } size && size.Width > 0)
			{
				return Math.Min(size.Width, MaxWidth);
			}

			try
			{
				// Headless consoles (CI runners) can report 0 without throwing; a
				// zero-width profile makes Spectre render nothing, so fall back.
				var width = Console.WindowWidth;
				return width > 0 ? Math.Min(width, MaxWidth) : FallbackWidth;
			}
			catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException)
			{
				return FallbackWidth;
			}
		}

		private static int ResolveHeight()
		{
			if (ReplSessionIO.WindowSize is { } size && size.Height > 0)
			{
				return Math.Min(size.Height, MaxHeight);
			}

			try
			{
				// Same headless-console guard as ResolveWidth.
				var height = Console.WindowHeight;
				return height > 0 ? Math.Min(height, MaxHeight) : FallbackHeight;
			}
			catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException)
			{
				return FallbackHeight;
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
