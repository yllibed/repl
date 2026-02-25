namespace Repl.Testing;

/// <summary>
/// Base event emitted while executing a command in the test harness.
/// </summary>
public abstract record CommandEvent(DateTimeOffset Timestamp);
