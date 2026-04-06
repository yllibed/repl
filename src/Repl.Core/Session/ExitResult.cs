namespace Repl;

internal sealed record ExitResult(int ExitCode, object? Payload) : IExitResult;
