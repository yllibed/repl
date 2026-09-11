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
	/// <param name="expected">The text to wait for.</param>
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
				throw new InvalidOperationException(
					Describe($"exited with code {_process.ExitCode} before writing '{expected}'"));
			}

			await Task.Delay(TimeSpan.FromMilliseconds(25), Clock, cancellationToken).ConfigureAwait(false);
		}

		throw new TimeoutException(Describe($"did not write '{expected}' within {_options.Timeout}"));
	}

	/// <summary>
	/// Delivers a real signal to the child through <c>kill</c>.
	/// </summary>
	/// <param name="signal">The signal to send. <see cref="ReplProcessSignal.Break"/> has no Unix equivalent and is refused.</param>
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

		// The overload without a token also drains the asynchronous output handlers, so everything the
		// child wrote is in Output by the time the exit code is read.
		_process.WaitForExit();
		return _process.ExitCode;
	}

	/// <summary>
	/// Kills the child and its descendants if anything is still running, then releases the process.
	/// A test that asserted an exit already has nothing left to kill; this is what keeps a failed
	/// assertion from leaking a process into the rest of the suite.
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
			_process.Kill(entireProcessTree: true);
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
		var startInfo = new ProcessStartInfo("kill")
		{
			UseShellExecute = false,
			RedirectStandardError = true,
		};
		startInfo.ArgumentList.Add($"-{number.ToString(CultureInfo.InvariantCulture)}");
		startInfo.ArgumentList.Add(_process.Id.ToString(CultureInfo.InvariantCulture));

		using var sender = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start 'kill' to deliver the signal.");
		await sender.WaitForExitAsync(cancellationToken)
			.WaitAsync(_options.Timeout, Clock, cancellationToken)
			.ConfigureAwait(false);
		if (sender.ExitCode == 0)
		{
			return;
		}

		var error = await sender.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		throw new InvalidOperationException(
			$"'kill -{number}' failed with exit code {sender.ExitCode} for process {_process.Id}: {error}");
	}

	private string Describe(string what) =>
		$"The probed process ({_process.StartInfo.FileName}) {what}."
		+ $"{System.Environment.NewLine}Captured output:{System.Environment.NewLine}{_capture.Read()}";

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	private sealed class OutputCapture
	{
		private readonly Lock _gate = new();
		private readonly StringBuilder _text = new();

		public void Append(string? line)
		{
			if (line is null)
			{
				return;
			}

			lock (_gate)
			{
				_text.AppendLine(line);
			}
		}

		public string Read()
		{
			lock (_gate)
			{
				return _text.ToString();
			}
		}
	}
}
