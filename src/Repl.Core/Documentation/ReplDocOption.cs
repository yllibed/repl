namespace Repl.Documentation;

/// <summary>
/// Option metadata.
/// </summary>
/// <remarks>
/// <c>DefaultValue</c> is display text: a collection's elements joined by <c>", "</c>, and <see langword="null"/>
/// when there is no default or the collection is empty.
/// </remarks>
public sealed record ReplDocOption(
	string Name,
	string Type,
	bool Required,
	string? Description,
	IReadOnlyList<string> Aliases,
	IReadOnlyList<string> ReverseAliases,
	IReadOnlyList<ReplDocValueAlias> ValueAliases,
	IReadOnlyList<string> EnumValues,
	string? DefaultValue)
{
	// Declared in the record body rather than as a positional parameter: this record is public and
	// its nine positional parameters have no defaults, so adding a tenth would change the
	// constructor signature and the Deconstruct arity for every already-compiled consumer.

	/// <summary>
	/// Gets a value indicating whether the option is hidden from discovery surfaces.
	/// </summary>
	/// <remarks>
	/// Mirrors <see cref="ReplDocCommand.IsHidden"/>: an aggregate model omits hidden options
	/// entirely, while a model built for an explicitly targeted command includes them with this
	/// flag set, so an app author can still inventory them. Hidden options remain parsable from the
	/// command line and the REPL; consumers that generate agent-facing schemas must omit them.
	/// </remarks>
	public bool IsHidden { get; init; }

	/// <summary>
	/// Gets a value indicating whether the option is suppressed on programmatic surfaces only.
	/// </summary>
	/// <remarks>
	/// Unlike <see cref="IsHidden"/>, an automation-hidden option is always present in this model —
	/// human-facing exports keep it, which is why the flag exists rather than an omission.
	/// Consumers that generate agent-facing schemas must omit it. Not an access-control boundary.
	/// </remarks>
	public bool IsAutomationHidden { get; init; }

}
