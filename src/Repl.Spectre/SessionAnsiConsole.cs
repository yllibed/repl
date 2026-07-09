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
		// The verdict here only shapes the profile (Unicode capability); the actual
		// transliteration decision is re-made per write inside SessionDelegatingTextWriter,
		// because a console captured early can serve a session activated later.
		var boxDrawing = ResolveBoxDrawingSupport(TryResolveSinkEncoding(ReplSessionIO.Output), IsLocalRedirected());
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

		return ApplyOptions(AnsiConsole.Create(settings), spectreOptions, boxDrawing);
	}

	/// <summary>
	/// Creates an <see cref="IAnsiConsole"/> that renders to the provided <see cref="TextWriter"/>.
	/// Used by the output transformer to capture rendered output as a string.
	/// </summary>
	public static IAnsiConsole CreateForWriter(TextWriter writer, int width, OutputOptions? outputOptions = null, SpectreConsoleOptions? spectreOptions = null)
	{
		// The support verdict comes from the FINAL sink (the capture writer is typically a
		// UTF-16 StringWriter whose content is later written to the session output), but the
		// transliteration must happen on the writer actually receiving the glyphs. The width
		// is untrusted (the transformer forwards the hosted client's advertised window size).
		var boxDrawing = ResolveBoxDrawingSupport(TryResolveSinkEncoding(ReplSessionIO.Output), IsLocalRedirected());
		var settings = new AnsiConsoleSettings
		{
			Out = new WriterAnsiConsoleOutput(
				boxDrawing == BoxDrawingSupport.Ascii ? new BoxDrawingTransliteratingWriter(writer) : writer,
				ClampWidth(width)),
			// Same rationale as Create: CI enrichers must not override the host detection.
			// A capture writer can never answer prompts.
			Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
			Interactive = InteractionSupport.No,
		};
		ApplyTerminalDetection(settings, outputOptions);

		return ApplyOptions(AnsiConsole.Create(settings), spectreOptions, boxDrawing);
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

	private static IAnsiConsole ApplyOptions(IAnsiConsole console, SpectreConsoleOptions? spectreOptions, BoxDrawingSupport boxDrawing)
	{
		console.Profile.Capabilities.Unicode =
			(spectreOptions ?? s_defaultOptions).Unicode && boxDrawing == BoxDrawingSupport.Rounded;
		return console;
	}

	internal static bool IsLocalRedirected() => !ReplSessionIO.IsHostedSession && Console.IsOutputRedirected;

	/// <summary>
	/// Reads a writer's declared encoding under guard: hosted transports can supply writers
	/// whose <see cref="TextWriter.Encoding"/> getter itself throws, and that read happens
	/// before the probe's own try/catch can help. <c>null</c> means "could not be read" and
	/// resolves to the Ascii tier.
	/// </summary>
	internal static Encoding? TryResolveSinkEncoding(TextWriter writer)
	{
		try
		{
			return writer.Encoding;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private const int FallbackWidth = 120;
	private const int FallbackHeight = 24;

	// Client-advertised sizes are untrusted input: a hosted client reporting int.MaxValue
	// would otherwise drive Spectre into width-proportional allocations. Shared by the
	// session output and the capture-writer output (the transformer path).
	private const int MaxWidth = 10_000;
	private const int MaxHeight = 1_000;

	internal static int ClampWidth(int width) => width > 0 ? Math.Min(width, MaxWidth) : FallbackWidth;

	internal static int ClampHeight(int height) => height > 0 ? Math.Min(height, MaxHeight) : FallbackHeight;

	/// <summary>
	/// Resolves how much box drawing the FINAL sink can carry. The support verdict is gated on
	/// the final sink's encoding, not any intermediate writer: the transformer renders into a
	/// UTF-16 StringWriter whose content is later written to the session output, so the session
	/// writer (or Console.Out locally — the fallback of ReplSessionIO.Output) is the encoding
	/// that actually has to carry the glyphs. Trial-encodes representative glyphs and checks the
	/// roundtrip: a legacy codepage with a best-fit/replacement fallback turns them into '?',
	/// which is exactly the mojibake this guards against — cheaper and more truthful than a
	/// codepage allowlist.
	/// </summary>
	internal static BoxDrawingSupport ResolveBoxDrawingSupport(Encoding? encoding, bool isLocalRedirected)
	{
		if (encoding is null)
		{
			// The sink's Encoding getter threw — nothing can be reasoned about the bytes it
			// will carry, so only charset-agnostic ASCII is safe.
			return BoxDrawingSupport.Ascii;
		}

		// A redirected local console on a non-Unicode codepage is undecodable by the reading
		// process no matter which glyph is picked: the writer emits single-byte OEM codes
		// (┌ is 0xDA in cp437) while modern readers (IDE run windows, CI logs, pipes) decode
		// UTF-8, where lone bytes ≥ 0x80 are invalid. Only ASCII is charset-agnostic there.
		// Field case: the Rider Run window (cp437, redirected) rendered every border byte as
		// U+FFFD. A non-redirected console is exempt — it decodes its own codepage.
		if (isLocalRedirected && !IsUnicodeCodePage(encoding))
		{
			return BoxDrawingSupport.Ascii;
		}

		var probes = GetProbes(encoding);
		if (probes.Rounded)
		{
			return BoxDrawingSupport.Rounded;
		}

		return probes.Square ? BoxDrawingSupport.Square : BoxDrawingSupport.Ascii;
	}

	// Reference-typed cache entry: a single reference write publishes both probe results
	// atomically, so a concurrent session can never observe a mismatched encoding/verdict
	// pair (the previous nullable-tuple field could tear under racing reads and writes).
	// Single entry: the sink encoding is constant per process/session, and Encoding
	// instances are effectively singletons.
	private sealed record BoxDrawingProbes(Encoding Encoding, bool Rounded, bool Square);

	private static BoxDrawingProbes? s_probeCache;

	private static BoxDrawingProbes GetProbes(Encoding encoding)
	{
		if (s_probeCache is { } cached && ReferenceEquals(cached.Encoding, encoding))
		{
			return cached;
		}

		// ╭ discriminates full Unicode sinks; ┌ discriminates OEM codepages (which carry the
		// square safe-border glyphs but not the rounded ones) from ASCII-only sinks.
		var probes = new BoxDrawingProbes(encoding, Roundtrips(encoding, "╭"), Roundtrips(encoding, "┌"));
		s_probeCache = probes;
		return probes;
	}

	private static bool IsUnicodeCodePage(Encoding encoding) =>
		encoding.CodePage is 65001 or 1200 or 1201 or 12000 or 12001;

	private static bool Roundtrips(Encoding encoding, string probe)
	{
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

		private static int ResolveWidth()
		{
			if (ReplSessionIO.WindowSize is { } size && size.Width > 0)
			{
				return ClampWidth(size.Width);
			}

			try
			{
				// Headless consoles (CI runners) can report 0 without throwing; a
				// zero-width profile makes Spectre render nothing, so fall back.
				return ClampWidth(Console.WindowWidth);
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
				return ClampHeight(size.Height);
			}

			try
			{
				// Same headless-console guard as ResolveWidth.
				return ClampHeight(Console.WindowHeight);
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

		public int Height => FallbackHeight;

		public void SetEncoding(Encoding encoding)
		{
			// Encoding is fixed by the capture writer handed in by the transformer.
		}
	}

	/// <summary>
	/// TextWriter that delegates all writes to <see cref="ReplSessionIO.Output"/>
	/// at call time, ensuring session-correct routing even if captured early. The
	/// box-drawing transliteration decision is made here too — per write, against the
	/// CURRENT sink — so a console created during a Unicode session cannot leak raw box
	/// glyphs into an ASCII session activated later.
	/// </summary>
	private sealed class SessionDelegatingTextWriter : TextWriter
	{
		// Maps the current raw sink to its effective (possibly transliterating) writer.
		// Reference-typed pair published atomically; a benign race recomputes the same value.
		private sealed record ResolvedTarget(TextWriter Raw, TextWriter Effective);

		private ResolvedTarget? _target;

		private TextWriter Target
		{
			get
			{
				var output = ReplSessionIO.Output;
				if (_target is { } cached && ReferenceEquals(cached.Raw, output))
				{
					return cached.Effective;
				}

				var support = ResolveBoxDrawingSupport(TryResolveSinkEncoding(output), IsLocalRedirected());
				var effective = support == BoxDrawingSupport.Ascii
					? new BoxDrawingTransliteratingWriter(output)
					: output;
				_target = new ResolvedTarget(output, effective);
				return effective;
			}
		}

		// A hosted writer's Encoding getter can itself throw; report UTF-8 rather than
		// crashing the caller — the box-drawing gate performs its own guarded read.
		public override Encoding Encoding => TryResolveSinkEncoding(ReplSessionIO.Output) ?? Encoding.UTF8;

		public override void Write(char value) => Target.Write(value);

		public override void Write(string? value) => Target.Write(value);

		public override void Write(char[] buffer, int index, int count) =>
			Target.Write(buffer, index, count);

		public override void WriteLine() => Target.WriteLine();

		public override void WriteLine(string? value) => Target.WriteLine(value);

		public override void Flush() => Target.Flush();

		public override Task WriteAsync(char value) => Target.WriteAsync(value);

		public override Task WriteAsync(string? value) => Target.WriteAsync(value);

		public override Task WriteLineAsync() => Target.WriteLineAsync();

		public override Task WriteLineAsync(string? value) => Target.WriteLineAsync(value);

		public override Task FlushAsync() => Target.FlushAsync();
	}
}
