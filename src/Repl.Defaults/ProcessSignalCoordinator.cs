using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Repl;

/// <summary>
/// Owns the process-wide standalone signal protocol. The first SIGINT (or SIGTERM on Unix)
/// atomically claims the current ownership epoch and prepares cancellation for every active scope;
/// later scopes join that draining epoch, and any subsequent signal is left to the operating-system
/// default. The epoch resets only after its final scope and all signal-triggered cancellation work
/// have drained. Interactive console-key ownership is selected first by
/// <see cref="ConsoleCancelKeyCoordinator"/>. Its gate is always released before this coordinator is
/// invoked. This coordinator may acquire an individual scope gate, but a scope never enters this
/// coordinator while holding its gate; reserved consumer callbacks start only after both gates are
/// released. Process registrations are installed lazily once and remain inert without active scopes.
/// </summary>
internal static class ProcessSignalCoordinator
{
	internal const int SigIntExitCode = 130;
	internal const int SigTermExitCode = 143;

	private static readonly Lock Gate = new();
	private static readonly HashSet<ProcessSignalCancellationScope> ActiveScopes = [];
	private static IDisposable? s_cancelKeyRegistration;
	private static PosixSignalRegistration? s_sigTermRegistration;
	private static ClaimedSignal? s_claimedSignal;
	private static int s_generation;
	private static int s_pendingDrainCount;
	private static bool s_registrationsInitialized;
	private static RegistrationFault? s_registrationFaultForTesting;

	/// <summary>
	/// Gives a test a coordinator with no installed registrations, optionally failing the next
	/// registration attempt, and leaves it able to install fresh ones on disposal.
	/// <para>
	/// Registrations capture the generation counter they were created under, so putting a saved
	/// registration object back after the counter has moved would leave it permanently stale and
	/// silently disable the bridge for the rest of the process. Both entering and leaving therefore
	/// tear the registrations down and let the next scope install new ones.
	/// </para>
	/// </summary>
	internal static IDisposable IsolateRegistrationsForTesting(
		Exception? registrationFault = null,
		bool faultAfterSigTermRegistration = false) =>
		new RegistrationIsolationScope(
			registrationFault is null
				? null
				: new RegistrationFault(registrationFault, faultAfterSigTermRegistration));

	internal static void Register(ProcessSignalCancellationScope scope)
	{
		ArgumentNullException.ThrowIfNull(scope);
		RegistrationOutcome outcome;
		Action? startCancellation = null;
		lock (Gate)
		{
			outcome = TryInitializeRegistrations();
			// The scope joins the epoch even when no bridge could be installed, so that disposal stays
			// symmetric and a run started before an earlier scope claimed a signal still inherits it.
			ActiveScopes.Add(scope);
			if (s_claimedSignal is { } claimedSignal)
			{
				startCancellation = scope.PrepareSignalCancellation(claimedSignal.ExitCode);
			}
		}

		// Installing the bridge is a convenience, not a precondition for running the command. Every way
		// it can fail to install — an unsupported platform, or an environment that refuses the
		// registration — degrades to caller-owned handling and says so once, on the same path.
		if (outcome.Diagnostic is { } diagnostic)
		{
			outcome.OrphanedCancelKeyRegistration?.Dispose();
			outcome.OrphanedSigTermRegistration?.Dispose();
			WriteDiagnostic(diagnostic);
		}

		startCancellation?.Invoke();
	}

	private static RegistrationOutcome TryInitializeRegistrations()
	{
		if (s_registrationsInitialized)
		{
			return default;
		}

		if (!IsSignalBridgeSupported())
		{
			s_registrationsInitialized = true;
			return new RegistrationOutcome(
				"Automatic process-signal handling is unavailable on this platform; "
				+ "the caller or platform host remains responsible for cancellation.",
				OrphanedCancelKeyRegistration: null,
				OrphanedSigTermRegistration: null);
		}

		PosixSignalRegistration? sigTermRegistration = null;
		IDisposable? cancelKeyRegistration = null;
		var generation = ++s_generation;
		try
		{
			// No supported platform rejects a signal registration on demand, so the failure policy
			// would otherwise be untestable. Tests set this to drive Register through the failing path,
			// either before anything is registered or after SIGTERM is, which is the only way to reach
			// the orphaned-registration cleanup below.
			ThrowIfFaultInjected(afterSigTermRegistration: false);

			if (!OperatingSystem.IsWindows())
			{
				sigTermRegistration = PosixSignalRegistration.Create(
					PosixSignal.SIGTERM,
					e => HandleSigTerm(generation, e));
			}

			ThrowIfFaultInjected(afterSigTermRegistration: true);

			cancelKeyRegistration = ConsoleCancelKeyCoordinator.RegisterStandalone(
				specialKey => HandleConsoleCancelKey(generation, specialKey));
			s_sigTermRegistration = sigTermRegistration;
			s_cancelKeyRegistration = cancelKeyRegistration;
			s_registrationsInitialized = true;
			return default;
		}
		catch (Exception ex)
		{
			// Invalidate callbacks created by the failed generation before releasing the gate.
			s_generation++;
			// Latch the attempt: without this every later run repeats a registration the environment
			// has already refused, and emits the same diagnostic once per run.
			s_registrationsInitialized = true;
			return new RegistrationOutcome(
				"Failed to install automatic process-signal handling: "
				+ $"{ex.GetType().Name}: {ex.Message}. "
				+ "The caller or platform host remains responsible for cancellation.",
				cancelKeyRegistration,
				sigTermRegistration);
		}
	}

	private static void ThrowIfFaultInjected(bool afterSigTermRegistration)
	{
		if (s_registrationFaultForTesting is { } fault
			&& fault.AfterSigTermRegistration == afterSigTermRegistration)
		{
			throw fault.Exception;
		}
	}

	internal static async Task<Exception?> UnregisterAsync(ProcessSignalCancellationScope scope)
	{
		ArgumentNullException.ThrowIfNull(scope);
		Task cancellationTask;
		lock (Gate)
		{
			cancellationTask = scope.MarkDisposedAndGetCancellationTaskAsync();
			ActiveScopes.Remove(scope);
			s_pendingDrainCount++;
		}

		Exception? cancellationCallbackException = null;
		try
		{
#pragma warning disable VSTHRD003 // Signal-triggered callbacks must drain before their epoch can reset.
			await cancellationTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}
		catch (Exception ex)
		{
			// This task exclusively represents CancellationToken callbacks reserved by the scope.
			cancellationCallbackException = ex;
		}
		finally
		{
			lock (Gate)
			{
				s_pendingDrainCount--;
				if (ActiveScopes.Count == 0 && s_pendingDrainCount == 0)
				{
					s_claimedSignal = null;
				}
			}
		}

		return cancellationCallbackException;
	}

	// Both keys cancel and both exit 130; only the operator-facing name differs, and reporting
	// Ctrl+Break as SIGINT contradicted the distinction this mode documents.
	private static ConsoleCancelKeyHandlingResult HandleConsoleCancelKey(
		int generation,
		ConsoleSpecialKey specialKey) =>
		TryClaimSignal(
			generation,
			specialKey == ConsoleSpecialKey.ControlBreak ? "Ctrl+Break" : "SIGINT",
			SigIntExitCode);

	private static void HandleSigTerm(int generation, PosixSignalContext e)
	{
		if (TryClaimSignal(generation, "SIGTERM", SigTermExitCode)
			== ConsoleCancelKeyHandlingResult.SuppressProcessTermination)
		{
			e.Cancel = true;
		}
	}

	private static ConsoleCancelKeyHandlingResult TryClaimSignal(
		int generation,
		string name,
		int exitCode)
	{
		ClaimedSignal? previousSignal;
		List<Action>? startCancellations = null;
		lock (Gate)
		{
			if (generation != s_generation)
			{
				return ConsoleCancelKeyHandlingResult.NotHandled;
			}

			previousSignal = s_claimedSignal;
			if (ActiveScopes.Count == 0 && previousSignal is null)
			{
				return ConsoleCancelKeyHandlingResult.NotHandled;
			}

			if (previousSignal is null)
			{
				s_claimedSignal = new ClaimedSignal(name, exitCode);
				startCancellations = [];
				foreach (var scope in ActiveScopes)
				{
					if (scope.PrepareSignalCancellation(exitCode) is { } startCancellation)
					{
						startCancellations.Add(startCancellation);
					}
				}
			}
		}

		if (previousSignal is { } claimedSignal)
		{
			WriteDiagnostic(
				$"Received {name} after {claimedSignal.Name}; allowing immediate operating-system termination.");
			return ConsoleCancelKeyHandlingResult.AllowProcessTermination;
		}

		if (startCancellations is not { } cancellations)
		{
			return ConsoleCancelKeyHandlingResult.NotHandled;
		}

		foreach (var startCancellation in cancellations)
		{
			startCancellation();
		}

		WriteDiagnostic(
			$"Received {name}; cancelling active standalone runs. Send the signal again to terminate immediately.");
		return ConsoleCancelKeyHandlingResult.SuppressProcessTermination;
	}

	private static bool IsSignalBridgeSupported() =>
		IsSignalBridgeSupportedForTesting(
			OperatingSystem.IsAndroid(),
			OperatingSystem.IsBrowser(),
			// Named explicitly rather than relied upon through IsIOS: Mac Catalyst is documented here as
			// unsupported, and OperatingSystem exposes it as its own guard, so the check states what it
			// means instead of resting on whether one platform predicate implies the other.
			OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst(),
			OperatingSystem.IsTvOS());

	internal static bool IsSignalBridgeSupportedForTesting(
		bool isAndroid,
		bool isBrowser,
		bool isIOSOrMacCatalyst,
		bool isTvOS) =>
		!isAndroid
		&& !isBrowser
		&& !isIOSOrMacCatalyst
		&& !isTvOS;

	[SuppressMessage(
		"Design",
		"CA1031:Do not catch general exception types",
		Justification = "This runs inside a signal callback, before it returns its suppression decision: any escaping exception replaces cooperative cleanup with immediate process termination, so a caller-supplied writer's failure of any type must be contained.")]
	internal static void WriteDiagnostic(string message)
	{
		try
		{
#pragma warning disable MA0045 // Process-signal callbacks must decide synchronously before the OS resumes default handling.
			ReplSessionIO.Error.WriteLine(message);
#pragma warning restore MA0045
		}
		catch
		{
			// Signal delivery must not fail because the diagnostic stream is unavailable — nor because
			// an application-supplied TextWriter threw something else. The suppression decision this
			// callback still owes the operating system matters more than the message.
		}
	}

	private sealed class RegistrationIsolationScope : IDisposable
	{
		public RegistrationIsolationScope(RegistrationFault? registrationFault) =>
			TearDownRegistrations(registrationFault);

		public void Dispose() => TearDownRegistrations(registrationFault: null);

		private static void TearDownRegistrations(RegistrationFault? registrationFault)
		{
			IDisposable? cancelKeyRegistration;
			PosixSignalRegistration? sigTermRegistration;
			lock (Gate)
			{
				cancelKeyRegistration = s_cancelKeyRegistration;
				sigTermRegistration = s_sigTermRegistration;
				s_cancelKeyRegistration = null;
				s_sigTermRegistration = null;
				// Uninstalled, so the next Register installs fresh registrations under a current
				// generation instead of reviving ones the counter has already left behind.
				s_registrationsInitialized = false;
				s_generation++;
				s_registrationFaultForTesting = registrationFault;
			}

			cancelKeyRegistration?.Dispose();
			sigTermRegistration?.Dispose();
		}
	}

	private readonly record struct ClaimedSignal(string Name, int ExitCode);

	/// <summary>
	/// The result of one registration attempt. A null <paramref name="Diagnostic"/> means the bridge is
	/// installed, or was already. Otherwise the bridge is not installed, the caller owns signals, and any
	/// registration created before the attempt failed is handed back for disposal outside the gate.
	/// </summary>
	private readonly record struct RegistrationOutcome(
		string? Diagnostic,
		IDisposable? OrphanedCancelKeyRegistration,
		PosixSignalRegistration? OrphanedSigTermRegistration);

	private readonly record struct RegistrationFault(
		Exception Exception,
		bool AfterSigTermRegistration);
}
