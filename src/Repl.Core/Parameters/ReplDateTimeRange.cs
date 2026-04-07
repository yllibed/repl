namespace Repl;

/// <summary>
/// Represents an inclusive date-time range from <see cref="From"/> to <see cref="To"/>.
/// Parsed from <c>start..end</c> or <c>start@duration</c> literals.
/// </summary>
public sealed record ReplDateTimeRange(DateTime From, DateTime To);
