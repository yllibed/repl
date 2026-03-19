namespace Repl;

/// <summary>
/// Declares an interactive answer slot on a command handler method.
/// Equivalent to calling <see cref="CommandBuilder.WithAnswer"/> in the fluent API.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AnswerAttribute(string name, string type = "string") : Attribute
{
	/// <summary>Answer name (matches the <c>name</c> parameter in <c>AskConfirmationAsync</c>, etc.).</summary>
	public string Name { get; } = name;

	/// <summary>Value type using route constraint names: <c>string</c>, <c>bool</c>, <c>int</c>, etc.</summary>
	public string Type { get; } = type;

	/// <summary>Optional description for help text and agent tool schemas.</summary>
	public string? Description { get; set; }
}
