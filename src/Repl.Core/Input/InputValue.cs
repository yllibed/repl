namespace Repl;

/// <summary>A typed input value and its availability.</summary>
/// <param name="State">Value availability.</param>
/// <param name="Value">Converted value when available.</param>
public sealed record InputValue(InputValueState State, object? Value = null);
