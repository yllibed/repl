namespace Repl;

/// <summary>
/// Where a local console prompt polls for keys while it holds <see cref="ConsoleInputGate"/>.
/// </summary>
/// <remarks>
/// Synchronous by design: <see cref="ConsoleKeyReader"/> acquires the gate itself, so a loop that already holds it
/// cannot use it.
/// </remarks>
internal readonly record struct LocalKeySource(Func<bool> KeyAvailable, Func<ConsoleKeyInfo> ReadKey)
{
	public static LocalKeySource SystemConsole { get; } =
		new(static () => Console.KeyAvailable, static () => Console.ReadKey(intercept: true));
}
