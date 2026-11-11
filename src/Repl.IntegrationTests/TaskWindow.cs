namespace Repl.IntegrationTests;

/// <summary>
/// Asks whether a task finishes within a window without cancelling it, for tests that assert a thing has
/// not happened yet.
/// </summary>
internal static class TaskWindow
{
	/// <summary>
	/// Returns whether <paramref name="task"/> completed within <paramref name="window"/>. The task is left running
	/// when the window elapses, and a task that faults still counts as completed: its exception is the caller's to observe.
	/// </summary>
	public static async Task<bool> CompletesWithinAsync(this Task task, TimeSpan window)
	{
		using var timer = new CancellationTokenSource();
#pragma warning disable VSTHRD003 // The caller hands over a task that is already running; this only watches it.
		var first = await Task.WhenAny(task, Task.Delay(window, timer.Token)).ConfigureAwait(false);
#pragma warning restore VSTHRD003

		// The delay is only a stopwatch: stop it so a task that finished early does not leave a timer behind.
		await timer.CancelAsync().ConfigureAwait(false);
		return ReferenceEquals(first, task);
	}
}
