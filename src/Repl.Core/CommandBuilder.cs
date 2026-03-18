namespace Repl;

/// <summary>
/// Configures metadata and behavior for a mapped command.
/// </summary>
public sealed class CommandBuilder
{
	private readonly Dictionary<string, CompletionDelegate> _completions =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Initializes a new instance of the <see cref="CommandBuilder"/> class.
	/// </summary>
	/// <param name="route">The command route template.</param>
	/// <param name="handler">The command handler delegate.</param>
	internal CommandBuilder(string route, Delegate handler)
	{
		Route = route;
		Handler = handler;
		SupportsHostedProtocolPassthrough = ComputeSupportsHostedProtocolPassthrough(handler);
	}

	/// <summary>
	/// Gets the command route.
	/// </summary>
	public string Route { get; }

	/// <summary>
	/// Gets the command handler.
	/// </summary>
	public Delegate Handler { get; }

	/// <summary>
	/// Gets the command description.
	/// </summary>
	public string? Description { get; private set; }

	/// <summary>
	/// Gets the configured aliases.
	/// </summary>
	public IReadOnlyList<string> Aliases { get; private set; } = [];

	/// <summary>
	/// Gets a value indicating whether this command is hidden from discovery surfaces.
	/// </summary>
	public bool IsHidden { get; private set; }

	/// <summary>
	/// Gets parameter completion providers keyed by target name.
	/// </summary>
	public IReadOnlyDictionary<string, CompletionDelegate> Completions => _completions;

	/// <summary>
	/// Gets a value indicating whether this command reserves stdin/stdout for a protocol handler.
	/// </summary>
	public bool IsProtocolPassthrough { get; private set; }

	/// <summary>
	/// Gets a value indicating whether the handler can run protocol passthrough in hosted sessions.
	/// </summary>
	internal bool SupportsHostedProtocolPassthrough { get; }

	/// <summary>
	/// Gets the banner delegate rendered before command execution.
	/// </summary>
	public Delegate? Banner { get; private set; }

	/// <summary>
	/// Gets the rich markdown description body.
	/// Used for agent tool descriptions and documentation export.
	/// </summary>
	public string? Details { get; private set; }

	/// <summary>
	/// Gets the structured behavioral annotations for this command.
	/// </summary>
	public CommandAnnotations? Annotations { get; private set; }

	/// <summary>
	/// Gets a value indicating whether this command is a resource (data to consult).
	/// </summary>
	public bool IsResource { get; private set; }

	/// <summary>
	/// Gets a value indicating whether this command is a prompt source.
	/// </summary>
	public bool IsPrompt { get; private set; }

	/// <summary>
	/// Gets generic metadata entries for extensibility.
	/// </summary>
	public IReadOnlyDictionary<string, object> Metadata => _metadata;

	private readonly Dictionary<string, object> _metadata = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Sets a command description.
	/// </summary>
	/// <param name="text">Description text.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder WithDescription(string text)
	{
		Description = string.IsNullOrWhiteSpace(text)
			? throw new ArgumentException("Description cannot be empty.", nameof(text))
			: text;
		return this;
	}

	/// <summary>
	/// Sets aliases for the command.
	/// </summary>
	/// <param name="aliases">Alias list.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder WithAlias(params string[] aliases)
	{
		ArgumentNullException.ThrowIfNull(aliases);

		var normalized = aliases
			.Where(alias => !string.IsNullOrWhiteSpace(alias))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (normalized.Length == 0)
		{
			throw new ArgumentException("At least one alias is required.", nameof(aliases));
		}

		Aliases = normalized;
		return this;
	}

	/// <summary>
	/// Adds a completion provider for a target parameter.
	/// </summary>
	/// <param name="targetName">Route or option target name.</param>
	/// <param name="provider">Completion delegate.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder WithCompletion(string targetName, CompletionDelegate provider)
	{
		targetName = string.IsNullOrWhiteSpace(targetName)
			? throw new ArgumentException("Target name cannot be empty.", nameof(targetName))
			: targetName;
		ArgumentNullException.ThrowIfNull(provider);

		_completions[targetName] = provider;
		return this;
	}

	/// <summary>
	/// Registers a banner delegate displayed before command execution.
	/// Unlike <see cref="WithDescription"/>, which is structural metadata visible in help and documentation,
	/// banners are display-only messages that appear at runtime.
	/// </summary>
	/// <param name="bannerProvider">Banner delegate with injectable parameters.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder WithBanner(Delegate bannerProvider)
	{
		ArgumentNullException.ThrowIfNull(bannerProvider);
		Banner = bannerProvider;
		return this;
	}

	/// <summary>
	/// Registers a static banner string displayed before command execution.
	/// Unlike <see cref="WithDescription"/>, which is structural metadata visible in help and documentation,
	/// banners are display-only messages that appear at runtime.
	/// </summary>
	/// <param name="text">Banner text.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder WithBanner(string text)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(text);
		Banner = () => text;
		return this;
	}

	/// <summary>
	/// Marks a command as hidden or visible.
	/// </summary>
	/// <param name="isHidden">True to hide the command.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder Hidden(bool isHidden = true)
	{
		IsHidden = isHidden;
		return this;
	}

	/// <summary>
	/// Marks this command as protocol passthrough.
	/// In this mode, repl diagnostics are routed to stderr and interactive stdin reads are skipped.
	/// When handlers request <see cref="IReplIoContext"/>, <see cref="IReplIoContext.Output"/> remains the protocol stream
	/// (stdout in local CLI passthrough), while framework output stays on stderr.
	/// For hosted sessions, handlers should request <see cref="IReplIoContext"/> to access transport streams explicitly.
	/// </summary>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder AsProtocolPassthrough()
	{
		IsProtocolPassthrough = true;
		return this;
	}

	// ── Rich metadata ──────────────────────────────────────────────────

	/// <summary>
	/// Sets a rich markdown description body for agent tool descriptions
	/// and documentation export.
	/// </summary>
	/// <param name="markdown">Markdown content.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder WithDetails(string markdown)
	{
		Details = string.IsNullOrWhiteSpace(markdown)
			? throw new ArgumentException("Details cannot be empty.", nameof(markdown))
			: markdown;
		return this;
	}

	/// <summary>
	/// Adds a generic metadata entry.
	/// </summary>
	/// <param name="key">Metadata key.</param>
	/// <param name="value">Metadata value.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder WithMetadata(string key, object value)
	{
		key = string.IsNullOrWhiteSpace(key)
			? throw new ArgumentException("Metadata key cannot be empty.", nameof(key))
			: key;
		ArgumentNullException.ThrowIfNull(value);
		_metadata[key] = value;
		return this;
	}

	// ── Annotation shortcuts ───────────────────────────────────────────
	// Same style as Hidden() — short, chainable, directly on CommandBuilder.
	// Uses `with` expressions to preserve record immutability.

	/// <summary>Marks the command as read-only (no side effects).</summary>
	/// <param name="value">True to mark as read-only.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder ReadOnly(bool value = true)
	{
		Annotations = (Annotations ?? new CommandAnnotations()) with { ReadOnly = value };
		return this;
	}

	/// <summary>Marks the command as destructive (deletes, modifies state).</summary>
	/// <param name="value">True to mark as destructive.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder Destructive(bool value = true)
	{
		Annotations = (Annotations ?? new CommandAnnotations()) with { Destructive = value };
		return this;
	}

	/// <summary>Marks the command as safely retriable.</summary>
	/// <param name="value">True to mark as idempotent.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder Idempotent(bool value = true)
	{
		Annotations = (Annotations ?? new CommandAnnotations()) with { Idempotent = value };
		return this;
	}

	/// <summary>Marks the command as interacting with external systems.</summary>
	/// <param name="value">True to mark as open-world.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder OpenWorld(bool value = true)
	{
		Annotations = (Annotations ?? new CommandAnnotations()) with { OpenWorld = value };
		return this;
	}

	/// <summary>Marks the command as long-running (enables task-based execution).</summary>
	/// <param name="value">True to mark as long-running.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder LongRunning(bool value = true)
	{
		Annotations = (Annotations ?? new CommandAnnotations()) with { LongRunning = value };
		return this;
	}

	/// <summary>Hides the command from programmatic/automation surfaces only.</summary>
	/// <param name="value">True to hide from automation.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder AutomationHidden(bool value = true)
	{
		Annotations = (Annotations ?? new CommandAnnotations()) with { AutomationHidden = value };
		return this;
	}

	/// <summary>
	/// Configures annotations via builder — escape hatch for complex scenarios.
	/// Overwrites any annotations set by individual shortcuts.
	/// </summary>
	/// <param name="configure">Builder configuration callback.</param>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder WithAnnotations(Action<CommandAnnotationsBuilder> configure)
	{
		ArgumentNullException.ThrowIfNull(configure);
		var builder = new CommandAnnotationsBuilder();
		configure(builder);
		Annotations = builder.Build();
		return this;
	}

	// ── Semantic markers ───────────────────────────────────────────────

	/// <summary>
	/// Marks this command as a resource (data to consult, not an operation to perform).
	/// Resources appear in a separate help section and are auto-exposed as MCP resources.
	/// </summary>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder AsResource()
	{
		IsResource = true;
		return this;
	}

	/// <summary>
	/// Marks this command as a prompt source.
	/// The handler return value becomes the prompt message template.
	/// Handler parameters become prompt arguments.
	/// </summary>
	/// <returns>The same builder instance.</returns>
	public CommandBuilder AsPrompt()
	{
		IsPrompt = true;
		return this;
	}

	private static bool ComputeSupportsHostedProtocolPassthrough(Delegate handler)
	{
		foreach (var parameter in handler.Method.GetParameters())
		{
			if (parameter.ParameterType != typeof(IReplIoContext))
			{
				continue;
			}

			// [FromContext] binds route/context values and is not stream injection.
			if (parameter.GetCustomAttributes(typeof(FromContextAttribute), inherit: true).Length > 0)
			{
				continue;
			}

			return true;
		}

		return false;
	}
}
