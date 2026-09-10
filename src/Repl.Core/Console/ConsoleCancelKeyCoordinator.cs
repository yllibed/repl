namespace Repl;

/// <summary>
/// Arbitrates console cancel-key ownership through one lazily installed, process-lifetime
/// <see cref="Console.CancelKeyPress"/> subscription. Interactive handlers take priority over
/// standalone handlers. Registration and removal are atomic with respect to selection snapshots.
/// If ownership changes before selection is validated, the coordinator reselects once; after
/// validation, the retained callbacks own that in-flight occurrence even if their registrations
/// are concurrently removed. No consumer callback runs while the coordinator gate is held.
/// </summary>
internal static class ConsoleCancelKeyCoordinator
{
	private static readonly Lock Gate = new();
	private static readonly Dictionary<long, Func<ConsoleSpecialKey, ConsoleCancelKeyHandlingResult>> InteractiveHandlers = [];
	private static readonly Dictionary<long, Func<ConsoleSpecialKey, ConsoleCancelKeyHandlingResult>> StandaloneHandlers = [];
	private static long s_nextRegistrationId;
	private static long s_registrationVersion;
	private static bool s_isSubscribed;

	internal static IDisposable RegisterInteractive(Func<ConsoleSpecialKey, ConsoleCancelKeyHandlingResult> handler) =>
		Register(handler, isInteractive: true);

	internal static IDisposable RegisterStandalone(Func<ConsoleSpecialKey, ConsoleCancelKeyHandlingResult> handler) =>
		Register(handler, isInteractive: false);

	private static Registration Register(
		Func<ConsoleSpecialKey, ConsoleCancelKeyHandlingResult> handler,
		bool isInteractive)
	{
		ArgumentNullException.ThrowIfNull(handler);
		lock (Gate)
		{
			EnsureSubscribed();
			var registrationId = ++s_nextRegistrationId;
			var handlers = isInteractive ? InteractiveHandlers : StandaloneHandlers;
			handlers.Add(registrationId, handler);
			s_registrationVersion++;
			return new Registration(registrationId, isInteractive);
		}
	}

	private static void EnsureSubscribed()
	{
		if (s_isSubscribed)
		{
			return;
		}

		Console.CancelKeyPress += OnCancelKeyPress;
		s_isSubscribed = true;
	}

	private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
	{
		if (HandleCancelKey(e.SpecialKey, OperatingSystem.IsWindows())
			== ConsoleCancelKeyHandlingResult.SuppressProcessTermination)
		{
			e.Cancel = true;
		}
	}

	internal static ConsoleCancelKeyHandlingResult HandleCancelKeyForTesting(
		ConsoleSpecialKey specialKey = ConsoleSpecialKey.ControlC,
		Action? afterInitialSelection = null,
		bool? isWindows = null) =>
		HandleCancelKey(specialKey, isWindows ?? OperatingSystem.IsWindows(), afterInitialSelection);

	private static ConsoleCancelKeyHandlingResult HandleCancelKey(
		ConsoleSpecialKey specialKey,
		bool isWindows,
		Action? afterInitialSelection = null)
	{
		if (!IsHandledCancelKey(specialKey, isWindows))
		{
			return ConsoleCancelKeyHandlingResult.NotHandled;
		}

		var selection = CaptureSelection();
		afterInitialSelection?.Invoke();
		return Invoke(RevalidateSelection(selection).Handlers, specialKey);
	}

	private static bool IsHandledCancelKey(ConsoleSpecialKey specialKey, bool isWindows) =>
		specialKey == ConsoleSpecialKey.ControlC
		|| (isWindows && specialKey == ConsoleSpecialKey.ControlBreak);

	private static DispatchSelection CaptureSelection()
	{
		lock (Gate)
		{
			return CaptureSelectionUnsafe();
		}
	}

	private static DispatchSelection RevalidateSelection(DispatchSelection selection)
	{
		lock (Gate)
		{
			return selection.Version == s_registrationVersion
				? selection
				: CaptureSelectionUnsafe();
		}
	}

	private static DispatchSelection CaptureSelectionUnsafe()
	{
		var handlers = InteractiveHandlers.Count > 0
			? InteractiveHandlers.Values
			: StandaloneHandlers.Values;
		return new DispatchSelection(s_registrationVersion, [.. handlers]);
	}

	private static ConsoleCancelKeyHandlingResult Invoke(
		IReadOnlyList<Func<ConsoleSpecialKey, ConsoleCancelKeyHandlingResult>> handlers,
		ConsoleSpecialKey specialKey)
	{
		var result = ConsoleCancelKeyHandlingResult.NotHandled;
		foreach (var handler in handlers)
		{
			result = handler(specialKey) switch
			{
				ConsoleCancelKeyHandlingResult.SuppressProcessTermination =>
					ConsoleCancelKeyHandlingResult.SuppressProcessTermination,
				ConsoleCancelKeyHandlingResult.AllowProcessTermination
					when result == ConsoleCancelKeyHandlingResult.NotHandled =>
					ConsoleCancelKeyHandlingResult.AllowProcessTermination,
				_ => result,
			};
		}

		return result;
	}

	private readonly record struct DispatchSelection(
		long Version,
		IReadOnlyList<Func<ConsoleSpecialKey, ConsoleCancelKeyHandlingResult>> Handlers);

	private sealed class Registration(long registrationId, bool isInteractive) : IDisposable
	{
		private int _disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
			{
				return;
			}

			lock (Gate)
			{
				var handlers = isInteractive ? InteractiveHandlers : StandaloneHandlers;
				if (handlers.Remove(registrationId))
				{
					s_registrationVersion++;
				}
			}
		}
	}
}
