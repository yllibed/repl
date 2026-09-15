using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Repl.Testing;

/// <summary>
/// Spawns an application and sends it real operating-system signals, for the guarantees an in-memory
/// test cannot reach: that the process actually terminates, with the exit code the shell sees, after
/// the cleanup it was given time to run.
/// <para>
/// Use <see cref="ReplProcessSignalHarness"/> for everything else. It is deterministic, fast, and
/// covers every decision the framework makes. This one exists for the part that is only true of a
/// real process, and it is correspondingly slower and platform-bound.
/// </para>
/// <para>
/// <b>Signals are sent on Unix only.</b> Delivering one to another process on Windows needs console
/// control events and console attachment rather than a signal, which this deliberately does not do —
/// see issue #83. <see cref="SendSignalAsync"/> throws <see cref="PlatformNotSupportedException"/>
/// there, and the rest of the probe still works, so a cross-platform suite can spawn and assert
/// everywhere and skip only the delivery.
/// </para>
/// </summary>
public sealed class ReplProcessProbe : IAsyncDisposable
{
	private const int SigInt = 2;
	private const int SigTerm = 15;

	// How long to wait for end-of-stream after the child exits. Bounded on purpose: a descendant that
	// inherited the child's redirected handles keeps the stream open for as long as it lives, so
	// end-of-stream may never arrive — in the one method whose entire job is to honour a deadline.
	private static readonly TimeSpan ExitDrainGrace = TimeSpan.FromMilliseconds(500);

	// Real time against a real process: there is no clock to fake when the thing being waited on is an
	// operating-system process, so the system provider is passed explicitly rather than left implicit.
	private static readonly TimeProvider Clock = TimeProvider.System;

	private readonly Process _process;
	private readonly OutputCapture _capture;
	private readonly ReplProcessProbeOptions _options;
	private bool _disposed;

	private ReplProcessProbe(Process process, OutputCapture capture, ReplProcessProbeOptions options)
	{
		_process = process;
		_capture = capture;
		_options = options;
	}

	/// <summary>The child's process id, which is what a signal is addressed to.</summary>
	public int ProcessId => _process.Id;

	/// <summary>
	/// Everything the child has written so far, standard output and standard error interleaved in the
	/// order they were read.
	/// <para>
	/// Output is drained continuously rather than at exit, so a child that fills its pipe is never
	/// blocked by this probe. What a killed child never got to flush is gone, though: to observe what
	/// happened during a shutdown that may not complete, have the application append to a file and
	/// read that instead.
	/// </para>
	/// </summary>
	public string Output => _capture.Read();

	/// <summary>
	/// Starts the process with its output drained from the moment it starts.
	/// </summary>
	/// <param name="fileName">The executable to run.</param>
	/// <param name="arguments">Arguments, passed without shell interpretation.</param>
	/// <param name="configure">Adjusts the probe options.</param>
	/// <returns>A running probe. Dispose it to make sure nothing is left behind.</returns>
	/// <exception cref="ArgumentException"><paramref name="fileName"/> is empty or whitespace.</exception>
	/// <exception cref="InvalidOperationException">The process could not be started.</exception>
	public static ReplProcessProbe Start(
		string fileName,
		IEnumerable<string>? arguments = null,
		Action<ReplProcessProbeOptions>? configure = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		var options = new ReplProcessProbeOptions();
		configure?.Invoke(options);

		var process = new Process { StartInfo = CreateStartInfo(fileName, arguments, options) };
		var capture = new OutputCapture();
		process.OutputDataReceived += (_, e) => capture.Append(e.Data);
		process.ErrorDataReceived += (_, e) => capture.Append(e.Data);
		try
		{
			if (!process.Start())
			{
				throw new InvalidOperationException($"Failed to start '{fileName}'.");
			}
		}
		catch
		{
			process.Dispose();
			throw;
		}

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		// Closed so a child that reads standard input sees end-of-input instead of waiting for a test
		// that is never going to write anything.
		process.StandardInput.Close();
		return new ReplProcessProbe(process, capture, options);
	}

	/// <summary>
	/// Waits until the child has written <paramref name="expected"/>, which is how a test knows the
	/// application has reached the point worth signalling rather than guessing with a delay.
	/// </summary>
	/// <param name="expected">
	/// The text to wait for. Redirected output is read a line at a time, so the child has to terminate
	/// the marker with a newline — <c>Console.WriteLine</c> does, a bare <c>Write</c> does not, and a
	/// marker left unterminated is not seen until the stream closes.
	/// </param>
	/// <param name="cancellationToken">Cancels the wait.</param>
	/// <exception cref="ArgumentException"><paramref name="expected"/> is empty or whitespace.</exception>
	/// <exception cref="InvalidOperationException">The child exited before writing it.</exception>
	/// <exception cref="TimeoutException">It did not appear within <see cref="ReplProcessProbeOptions.Timeout"/>.</exception>
	public async ValueTask WaitForOutputAsync(string expected, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expected);
		ThrowIfDisposed();

		var deadline = Clock.GetUtcNow() + _options.Timeout;
		while (Clock.GetUtcNow() < deadline)
		{
			if (_capture.Read().Contains(expected, StringComparison.Ordinal))
			{
				return;
			}

			if (_process.HasExited)
			{
				// Exiting does not mean the capture is complete: the last line may still be sitting in an
				// asynchronous callback. Let it settle, then look again before calling this a failure.
				await DrainAfterExitAsync(deadline, cancellationToken).ConfigureAwait(false);
				if (_capture.Read().Contains(expected, StringComparison.Ordinal))
				{
					return;
				}

				throw new InvalidOperationException(
					Describe($"exited with code {_process.ExitCode} before writing '{expected}'"));
			}

			await Task.Delay(TimeSpan.FromMilliseconds(25), Clock, cancellationToken).ConfigureAwait(false);
		}

		// One last look before giving up: text written during the final delay lands after the loop
		// condition was evaluated, and rejecting it would fail a wait whose own error message contains
		// the very marker it says never arrived.
		if (_capture.Read().Contains(expected, StringComparison.Ordinal))
		{
			return;
		}

		throw new TimeoutException(Describe($"did not write '{expected}' within {_options.Timeout}"));
	}

	/// <summary>
	/// Delivers a real signal to the child through <c>kill</c>.
	/// </summary>
	/// <param name="signal">The signal to send. <see cref="ReplProcessSignal.Break"/> has no Unix equivalent and is refused.</param>
	/// <remarks>
	/// The child is checked for having exited before the signal is sent, but the two cannot be made
	/// atomic without a process handle the operating system keeps alive: if the child exits in between
	/// and its id is reused, the signal reaches whatever inherited that id. The window is small and the
	/// check narrows it, but on a busy host it is not zero — the same limitation that applies to killing
	/// a process tree during disposal.
	/// </remarks>
	/// <param name="cancellationToken">Cancels waiting for the sender to finish.</param>
	/// <exception cref="PlatformNotSupportedException">Running on Windows, or <paramref name="signal"/> is <see cref="ReplProcessSignal.Break"/>.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="signal"/> is not a known signal.</exception>
	/// <exception cref="InvalidOperationException">The child had already exited, or <c>kill</c> refused the signal.</exception>
	public async ValueTask SendSignalAsync(
		ReplProcessSignal signal,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		if (OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"Sending a signal to another process on Windows needs a console control event and "
				+ "console attachment rather than a signal, which this probe deliberately does not do. "
				+ "Use ReplProcessSignalHarness to assert the framework's decisions on Windows; see "
				+ "issue #83 for the Windows lifecycle work.");
		}

		var number = signal switch
		{
			ReplProcessSignal.Interrupt => SigInt,
			ReplProcessSignal.Terminate => SigTerm,
			ReplProcessSignal.Break => throw new PlatformNotSupportedException(
				"Ctrl+Break is a Windows console event with no Unix signal the framework treats the "
				+ "same way. Deliver it in memory with ReplProcessSignalHarness instead."),
			_ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown process signal."),
		};

		// Signalling a process that has already exited would either do nothing or, once the id is
		// reused, reach something else entirely.
		if (_process.HasExited)
		{
			throw new InvalidOperationException(
				Describe($"had already exited with code {_process.ExitCode} when {signal} was sent"));
		}

		await SendAsync(number, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Waits for the child to exit and returns its exit code — the one a shell would report.
	/// </summary>
	/// <param name="cancellationToken">Cancels the wait.</param>
	/// <returns>The exit code.</returns>
	/// <exception cref="TimeoutException">It did not exit within <see cref="ReplProcessProbeOptions.Timeout"/>.</exception>
	public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		try
		{
			await _process.WaitForExitAsync(cancellationToken)
				.WaitAsync(_options.Timeout, Clock, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (TimeoutException ex)
		{
			throw new TimeoutException(Describe($"did not exit within {_options.Timeout}"), ex);
		}

		// Settles the asynchronous readers so everything the child wrote is in Output by the time the exit
		// code is read — bounded, for the same reason as the drain in WaitForOutputAsync.
		await DrainAfterExitAsync(Clock.GetUtcNow() + _options.Timeout, cancellationToken).ConfigureAwait(false);
		return _process.ExitCode;
	}

	/// <summary>
	/// Kills the child and its descendants if the child is still running, then releases the process.
	/// A test that asserted an exit already has nothing left to kill; this is what keeps a failed
	/// assertion from leaking a process into the rest of the suite.
	/// <para>
	/// The tree is reachable only while its root is: a child that spawned something long-lived and then
	/// exited on its own leaves that descendant running, because there is no longer a parent to walk
	/// down from. Holding descendants beyond the parent needs a job object or a process group, which is
	/// platform-specific and deliberately not done here — so a probed application that forks background
	/// work has to clean up after itself. Walking the tree has the same residual risk as signalling:
	/// .NET's Unix implementation matches descendants by process id without a start-time check, so an id
	/// reused before cleanup runs belongs to whoever inherited it.
	/// </para>
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		if (!_process.HasExited)
		{
			TryKill(_process);
			await _process.WaitForExitAsync().ConfigureAwait(false);
		}

		_process.Dispose();
	}

	private static ProcessStartInfo CreateStartInfo(
		string fileName,
		IEnumerable<string>? arguments,
		ReplProcessProbeOptions options)
	{
		var startInfo = new ProcessStartInfo(fileName)
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		if (arguments is not null)
		{
			foreach (var argument in arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}
		}

		foreach (var pair in options.Environment)
		{
			startInfo.Environment[pair.Key] = pair.Value;
		}

		if (options.WorkingDirectory is { } workingDirectory)
		{
			startInfo.WorkingDirectory = workingDirectory;
		}

		return startInfo;
	}

	private async ValueTask SendAsync(int number, CancellationToken cancellationToken)
	{
		// An absolute path rather than a bare name. Started without a shell, a bare name is resolved
		// through PATH, so a consumer's CI step that prepends a directory — a third-party action, say —
		// decides which binary receives the signal. That is a trust decision this package should not be
		// making on the caller's behalf. Falls back to the name only if neither standard location exists,
		// so an unusual layout still works.
		var startInfo = new ProcessStartInfo(ResolveKillPath())
		{
			UseShellExecute = false,
			RedirectStandardError = true,
		};
		startInfo.ArgumentList.Add($"-{number.ToString(CultureInfo.InvariantCulture)}");
		startInfo.ArgumentList.Add(_process.Id.ToString(CultureInfo.InvariantCulture));

		using var sender = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start 'kill' to deliver the signal.");
		try
		{
			await sender.WaitForExitAsync(cancellationToken)
				.WaitAsync(_options.Timeout, Clock, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (TimeoutException ex)
		{
			// Disposing the sender would not stop it, and a bare timeout here would drop the captured
			// output every other wait on this type promises.
			TryKill(sender);
			throw new TimeoutException(Describe($"was still being signalled when 'kill -{number}' timed out"), ex);
		}
		catch (OperationCanceledException)
		{
			// Same reasoning: the sender outlives its wrapper, so giving up on the wait without killing it
			// leaves a helper process behind for whoever cancelled.
			TryKill(sender);
			throw;
		}
		if (sender.ExitCode == 0)
		{
			return;
		}

		var error = await sender.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		throw new InvalidOperationException(
			$"'kill -{number}' failed with exit code {sender.ExitCode} for process {_process.Id}: {error}");
	}

	/// <summary>
	/// Waits for both readers to report end-of-stream, bounded by <see cref="ExitDrainGrace"/> and by
	/// <paramref name="deadline"/>.
	/// <para>
	/// End-of-stream rather than a quiet interval: a callback delayed by a busy thread pool looks
	/// exactly like a stream with nothing left in it, so treating quiet as drained would reject output
	/// the child had already written — intermittently, and on a loaded CI machine first.
	/// </para>
	/// <para>
	/// Bounded rather than <see cref="Process.WaitForExit()"/>, which waits for the same signal without
	/// a deadline: a descendant that inherited the redirected handles holds them open until it exits,
	/// so that drain outlives the process it was draining and every deadline above it stops meaning
	/// anything. Giving up on the grace keeps the deadline; it costs nothing in the normal case, where
	/// the streams close as the child exits and this returns at once.
	/// </para>
	/// </summary>
	private async Task DrainAfterExitAsync(DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		var grace = ExitDrainGrace;
		var untilDeadline = deadline - Clock.GetUtcNow();
		if (untilDeadline < grace)
		{
			grace = untilDeadline;
		}

		if (grace <= TimeSpan.Zero)
		{
			return;
		}

		try
		{
#pragma warning disable VSTHRD003 // Completed by this probe's own readers, wired in Start.
			await _capture.Drained.WaitAsync(grace, Clock, cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}
		catch (TimeoutException)
		{
			// A live descendant still holds the handles. Report what did arrive rather than break the
			// deadline waiting for a stream nothing is going to close.
		}
	}

	private static string ResolveKillPath()
	{
		foreach (var candidate in (string[])["/bin/kill", "/usr/bin/kill"])
		{
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return "kill";
	}

	private static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// It exited between the check and the kill. That is the outcome this wanted anyway, and
			// turning a won race into a teardown failure would fail tests that had already passed.
		}
	}

	private string Describe(string what) =>
		$"The probed process ({_process.StartInfo.FileName}) {what}."
		+ $"{System.Environment.NewLine}Captured output:{System.Environment.NewLine}{_capture.Read()}";

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	private sealed class OutputCapture
	{
		private readonly Lock _gate = new();
		private readonly StringBuilder _text = new();
		// Completed once both redirected streams have reported end-of-stream, which is the only exact
		// evidence that nothing more is coming. Continuations run asynchronously so a waiter cannot be
		// resumed on the reader thread that is still delivering the other stream.
		private readonly TaskCompletionSource _drained =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _openStreams = 2;
		// Materialised once per change rather than once per read: WaitForOutputAsync reads every 25ms,
		// and copying the whole buffer each time costs more the longer the child talks.
		private string? _materialized;

		/// <summary>
		/// Completes when standard output and standard error have both reached end-of-stream.
		/// </summary>
		public Task Drained => _drained.Task;

		public void Append(string? line)
		{
			if (line is null)
			{
				// .NET raises the handler once per stream with no data to mark end-of-stream. TrySetResult
				// rather than SetResult: the count is the guard, and a second completion attempt must not
				// turn a drained capture into a crashed reader thread.
				if (Interlocked.Decrement(ref _openStreams) <= 0)
				{
					_drained.TrySetResult();
				}

				return;
			}

			lock (_gate)
			{
				_text.AppendLine(line);
				_materialized = null;
			}
		}

		public string Read()
		{
			lock (_gate)
			{
				return _materialized ??= _text.ToString();
			}
		}
	}
}
