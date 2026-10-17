namespace Repl;

/// <summary>Availability of a typed value without resolving a command handler's services.</summary>
public enum InputValueState
{
	/// <summary>The value converted successfully.</summary>
	Available,
	/// <summary>The input does not yet supply a value.</summary>
	Missing,
	/// <summary>The supplied value could not be converted.</summary>
	Invalid,
}
