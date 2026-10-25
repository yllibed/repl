namespace Repl;

/// <summary>A range in the original input text, measured in UTF-16 code units.</summary>
/// <param name="Start">Zero-based start position.</param>
/// <param name="Length">Number of code units.</param>
public sealed record InputSourceRange(int Start, int Length);
