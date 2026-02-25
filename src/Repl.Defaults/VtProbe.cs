using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Repl;

/// <summary>
/// Probes a remote terminal client for VT/ANSI support and window size
/// by sending Device Attributes (DA) and window size queries through an <see cref="IReplHost"/>.
/// Must be called <b>before</b> the REPL loop starts.
/// </summary>
internal static partial class VtProbe
{
	[StructLayout(LayoutKind.Auto)]
	internal readonly record struct VtProbeResult(bool SupportsAnsi, int? Width, int? Height);

	/// <summary>
	/// Sends DA + window size queries and waits for a response.
	/// </summary>
	public static async ValueTask<VtProbeResult> DetectAsync(
		IReplHost host,
		CancellationToken cancellationToken,
		int timeoutMs = 500)
	{
		// \x1b[c = Primary Device Attributes, \x1b[18t = Report window size (rows/cols).
		await host.Output.WriteAsync("\x1b[c\x1b[18t").ConfigureAwait(false);
		await host.Output.FlushAsync(cancellationToken).ConfigureAwait(false);

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeoutMs);

		var buffer = new char[256];
		var totalRead = 0;
		try
		{
			// Read responses until timeout (responses may arrive in 1 or more chunks).
			while (totalRead < buffer.Length)
			{
				var read = await host.Input.ReadAsync(
					buffer.AsMemory(totalRead),
					timeoutCts.Token).ConfigureAwait(false);

				if (read == 0)
				{
					break;
				}

				totalRead += read;
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// Timeout — evaluate whatever we received so far.
		}

		var response = new string(buffer, 0, totalRead);
		var supportsAnsi = response.Contains("\x1b[?", StringComparison.Ordinal);
		var (width, height) = ParseWindowSize(response);
		return new VtProbeResult(supportsAnsi, width, height);
	}

	private static (int? Width, int? Height) ParseWindowSize(string response)
	{
		// Match \x1b[8;{rows};{cols}t
		var match = WindowSizePattern().Match(response);
		if (!match.Success)
		{
			return (null, null);
		}

		return (
			int.Parse(match.Groups["cols"].Value, CultureInfo.InvariantCulture),
			int.Parse(match.Groups["rows"].Value, CultureInfo.InvariantCulture));
	}

	[GeneratedRegex(@"\x1b\[8;(?<rows>\d+);(?<cols>\d+)t", RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
	private static partial Regex WindowSizePattern();
}
