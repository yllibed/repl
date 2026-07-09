namespace Repl.Spectre;

/// <summary>
/// Read-only view of the terminal detection Spectre rendering runs on. "Ask the running
/// app first": diagnostics commands display these values instead of re-implementing the
/// framework's probes (which would drift).
/// </summary>
public static class SpectreTerminalDetection
{
	/// <summary>
	/// Gets the box-drawing verdict resolved for the active session output — the exact
	/// verdict console creation uses: <see cref="BoxDrawingSupport.Rounded"/> borders,
	/// Spectre's <see cref="BoxDrawingSupport.Square"/> safe borders, or
	/// <see cref="BoxDrawingSupport.Ascii"/> transliteration.
	/// </summary>
	public static BoxDrawingSupport CurrentBoxDrawingSupport =>
		SessionAnsiConsole.ResolveBoxDrawingSupport(
			SessionAnsiConsole.TryResolveSinkEncoding(ReplSessionIO.Output),
			SessionAnsiConsole.IsLocalRedirected());
}
