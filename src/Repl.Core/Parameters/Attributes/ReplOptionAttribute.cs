namespace Repl.Parameters;

/// <summary>
/// Configures named option metadata for a handler parameter.
/// </summary>
/// <remarks>
/// A parameter Repl supplies, such as <see cref="CancellationToken"/> or <see cref="IReplInteractionChannel"/>, is
/// never an option, so mapping a command fails when this attribute is declared on one.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ReplOptionAttribute : Attribute
{
	/// <summary>
	/// Canonical option name without prefix.
	/// </summary>
	public string? Name { get; set; }

	/// <summary>
	/// Additional option aliases as full tokens (for example: <c>--mode</c>, <c>-m</c>).
	/// </summary>
	public string[] Aliases { get; set; } = [];

	/// <summary>
	/// Legacy aliases that remain accepted by parsing and binding but are omitted from every
	/// discovery surface. Values are full tokens (for example: <c>--old-mode</c> or <c>-o</c>).
	/// </summary>
	/// <remarks>
	/// Use this for backwards-compatible migrations where <see cref="Name"/> stays visible while an
	/// old spelling remains callable. This is not an access-control boundary.
	/// </remarks>
	public string[] HiddenAliases { get; set; } = [];

	/// <summary>
	/// Reverse aliases as full tokens (for example: <c>--no-verbose</c>).
	/// </summary>
	public string[] ReverseAliases { get; set; } = [];

	/// <summary>
	/// Binding mode for the parameter.
	/// </summary>
	public ReplParameterMode Mode { get; set; } = ReplParameterMode.OptionAndPositional;

	/// <summary>
	/// Gets or sets a value indicating whether the option is hidden from every discovery surface:
	/// help, interactive and shell completion including value providers, exported documentation,
	/// and generated MCP tool and prompt schemas.
	/// </summary>
	/// <remarks>
	/// Parsing and binding are unaffected, so the option still binds when a command-line or REPL
	/// caller supplies it explicitly. An MCP <c>tools/call</c> that supplies it is rejected, because
	/// the option is absent from the advertised tool schema. A hidden option must be optional.
	/// This is a discovery filter, <b>not</b> an access-control boundary: the token remains an
	/// invocable part of the command line for anyone who knows it.
	/// </remarks>
	public bool Hidden { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether the option is hidden from programmatic surfaces only.
	/// </summary>
	/// <remarks>
	/// Unlike <see cref="Hidden"/>, which hides from all surfaces, this suppresses only MCP tool
	/// input schemas and prompt arguments — and, because the advertised schema and the accepted
	/// argument list share one option list, an MCP <c>tools/call</c> that supplies the option is
	/// rejected too. The option stays visible in help, in interactive and shell completion, and in
	/// exported documentation, and binds normally from the command line and the REPL. Mirrors
	/// <see cref="CommandAnnotations.AutomationHidden"/> at the option level. Not supported on typed
	/// global-options properties, which never reach a programmatic surface. This is a discovery
	/// filter, <b>not</b> an access-control boundary.
	/// </remarks>
	public bool AutomationHidden { get; set; }

	// Nullable enums are not legal attribute named arguments (CS0655), so the optional
	// overrides expose a non-nullable property and track the unset state in a nullable
	// backing field surfaced through the read-only *Override properties.
	private ReplCaseSensitivity? _caseSensitivity;
	private ReplArity? _arity;

	/// <summary>
	/// Optional case-sensitivity override for this option.
	/// Only an explicit assignment overrides the global parsing default; when unset, the getter
	/// returns the enum default and does not reflect the effective behavior — read
	/// <see cref="CaseSensitivityOverride"/> to distinguish unset from an explicit value.
	/// </summary>
	public ReplCaseSensitivity CaseSensitivity
	{
		get => _caseSensitivity ?? default;
		set => _caseSensitivity = value;
	}

	/// <summary>
	/// Explicit case-sensitivity override, or null to inherit the global default.
	/// </summary>
	public ReplCaseSensitivity? CaseSensitivityOverride => _caseSensitivity;

	/// <summary>
	/// Optional arity override.
	/// Only an explicit assignment overrides the arity inferred from the parameter shape; when
	/// unset, the getter returns the enum default and does not reflect the effective arity — read
	/// <see cref="ArityOverride"/> to distinguish unset from an explicit value.
	/// </summary>
	public ReplArity Arity
	{
		get => _arity ?? default;
		set => _arity = value;
	}

	/// <summary>
	/// Explicit arity override, or null to use the arity inferred from the parameter shape.
	/// </summary>
	public ReplArity? ArityOverride => _arity;
}
