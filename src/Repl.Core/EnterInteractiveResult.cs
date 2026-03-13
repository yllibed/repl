namespace Repl;

/// <summary>
/// Signals that the process should enter interactive REPL mode after rendering the payload.
/// </summary>
/// <param name="Payload">Optional result payload to render before entering interactive mode.</param>
public sealed record EnterInteractiveResult(object? Payload);
