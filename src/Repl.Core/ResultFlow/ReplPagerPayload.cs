namespace Repl;

/// <summary>
/// Represents a rendered result-flow payload supplied to a pager renderer.
/// </summary>
/// <param name="Payload">Rendered payload text.</param>
/// <param name="HasMore">Whether another result-flow payload can be fetched.</param>
public sealed record ReplPagerPayload(string Payload, bool HasMore);
