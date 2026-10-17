namespace Repl;

/// <summary>Availability of a typed input value in the parsed snapshot.</summary>
public enum InputValueState
{
	/// <summary>The value converted successfully.</summary>
	Available,
	/// <summary>The input does not yet supply a value.</summary>
	Missing,
	/// <summary>The supplied value could not be converted.</summary>
	Invalid,
}
