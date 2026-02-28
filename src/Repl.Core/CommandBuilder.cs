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
