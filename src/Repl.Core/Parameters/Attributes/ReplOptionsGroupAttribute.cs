namespace Repl.Parameters;

/// <summary>
/// Marks a class as a reusable options group whose public properties are
/// expanded into individual command options when used as a handler parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ReplOptionsGroupAttribute : Attribute
{
}
