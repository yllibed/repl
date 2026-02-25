using System.Text.Json;

namespace Repl;

/// <summary>
/// Output pipeline configuration.
/// </summary>
public sealed class OutputOptions
{
	private readonly Dictionary<string, IOutputTransformer> _transformers =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _aliases =
		new(StringComparer.OrdinalIgnoreCase);
	private Func<bool> _resolveHostAnsiSupport = static () => true;

	/// <summary>
	/// Initializes a new instance of the <see cref="OutputOptions"/> class.
	/// </summary>
	public OutputOptions()
	{
		JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
		{
			WriteIndented = true,
		};

		_transformers["human"] = new HumanOutputTransformer(ResolveHumanRenderSettings);
		_transformers["json"] = new JsonOutputTransformer(JsonSerializerOptions);
		_transformers["xml"] = new XmlOutputTransformer(JsonSerializerOptions);
		_transformers["yaml"] = new YamlOutputTransformer(JsonSerializerOptions);
		_transformers["markdown"] = new MarkdownOutputTransformer();

		_aliases["json"] = "json";
		_aliases["xml"] = "xml";
		_aliases["yaml"] = "yaml";
		_aliases["yml"] = "yaml";
		_aliases["markdown"] = "markdown";
	}

	/// <summary>
	/// Gets or sets the default output format.
	/// </summary>
	public string DefaultFormat { get; set; } = "human";

	/// <summary>
	/// Gets or sets the ANSI mode behavior.
	/// </summary>
	public AnsiMode AnsiMode { get; set; } = AnsiMode.Auto;

	/// <summary>
	/// Gets or sets the theme mode used for ANSI palette selection.
	/// </summary>
	public ThemeMode ThemeMode { get; set; } = ThemeMode.Auto;

	/// <summary>
	/// Gets or sets the palette provider used when ANSI rendering is enabled.
	/// </summary>
	public IAnsiPaletteProvider PaletteProvider { get; set; } = new DefaultAnsiPaletteProvider();

	/// <summary>
	/// Gets or sets a value indicating whether banner output is enabled.
	/// </summary>
	public bool BannerEnabled { get; set; } = true;

	/// <summary>
	/// Gets or sets a value indicating whether structured output (for example JSON)
	/// should be ANSI colorized during interactive sessions.
	/// </summary>
	public bool ColorizeStructuredInteractive { get; set; } = true;

	/// <summary>
	/// Gets or sets a preferred render width for human output.
	/// </summary>
	/// <remarks>
	/// When null, width is resolved from the current terminal when available.
	/// </remarks>
	public int? PreferredWidth { get; set; }

	/// <summary>
	/// Gets or sets the fallback render width when terminal width is unavailable.
	/// </summary>
	public int FallbackWidth { get; set; } = 120;

	/// <summary>
	/// Gets JSON serializer options used by the JSON transformer.
	/// </summary>
	public JsonSerializerOptions JsonSerializerOptions { get; }

	/// <summary>
	/// Gets registered transformers by name.
	/// </summary>
	public IReadOnlyDictionary<string, IOutputTransformer> Transformers => _transformers;

	/// <summary>
	/// Gets registered output aliases by alias name.
	/// </summary>
	public IReadOnlyDictionary<string, string> Aliases => _aliases;

	/// <summary>
	/// Adds a transformer for an output format.
	/// </summary>
	/// <param name="name">Format name.</param>
	/// <param name="transformer">Transformer implementation.</param>
	public void AddTransformer(string name, IOutputTransformer transformer)
	{
		name = string.IsNullOrWhiteSpace(name)
			? throw new ArgumentException("Transformer name cannot be empty.", nameof(name))
			: name;
		ArgumentNullException.ThrowIfNull(transformer);

		if (_transformers.ContainsKey(name))
		{
			throw new InvalidOperationException(
				$"A transformer named '{name}' is already registered.");
		}

		_transformers[name] = transformer;
	}

	/// <summary>
	/// Adds a flag alias mapped to an output format.
	/// </summary>
	/// <param name="alias">Alias name without prefix (for example: "markdown").</param>
	/// <param name="format">Target output format name.</param>
	public void AddAlias(string alias, string format)
	{
		alias = string.IsNullOrWhiteSpace(alias)
			? throw new ArgumentException("Alias cannot be empty.", nameof(alias))
			: alias;
		format = string.IsNullOrWhiteSpace(format)
			? throw new ArgumentException("Format cannot be empty.", nameof(format))
			: format;
		if (_aliases.ContainsKey(alias))
		{
			throw new InvalidOperationException($"An output alias named '{alias}' is already registered.");
		}

		_aliases[alias] = format;
	}

	internal bool TryResolveAlias(string alias, out string format) =>
		_aliases.TryGetValue(alias, out format!);

	internal void SetHostAnsiSupportResolver(Func<bool> resolver)
	{
		ArgumentNullException.ThrowIfNull(resolver);
		_resolveHostAnsiSupport = resolver;
	}

	internal HumanRenderSettings ResolveHumanRenderSettings() =>
		new(
			Width: ResolveRenderWidth(),
			UseAnsi: IsAnsiEnabled(),
			Palette: ResolvePalette());

	internal bool IsAnsiEnabled()
	{
		if (ReplSessionIO.IsSessionActive && ReplSessionIO.AnsiSupport is { } sessionAnsi)
		{
			return sessionAnsi;
		}

		if (AnsiMode == AnsiMode.Always)
		{
			return true;
		}

		if (AnsiMode == AnsiMode.Never || !_resolveHostAnsiSupport())
		{
			return false;
		}

		if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NO_COLOR")))
		{
			return false;
		}

		if (string.Equals(Environment.GetEnvironmentVariable("CLICOLOR_FORCE"), "1", StringComparison.Ordinal))
		{
			return true;
		}

		if (Console.IsOutputRedirected)
		{
			return false;
		}

		var term = Environment.GetEnvironmentVariable("TERM");
		if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return true;
	}

	internal AnsiPalette ResolvePalette()
	{
		var provider = PaletteProvider ?? new DefaultAnsiPaletteProvider();
		var theme = ResolveThemeMode();
		return provider.Create(theme);
	}

	private ThemeMode ResolveThemeMode()
	{
		if (ThemeMode != ThemeMode.Auto)
		{
			return ThemeMode;
		}

		try
		{
			return IsDarkConsoleColor(Console.BackgroundColor) ? ThemeMode.Dark : ThemeMode.Light;
		}
		catch
		{
			return ThemeMode.Dark;
		}
	}

	private static bool IsDarkConsoleColor(ConsoleColor color) =>
		color is ConsoleColor.Black
			or ConsoleColor.DarkBlue
			or ConsoleColor.DarkCyan
			or ConsoleColor.DarkGray
			or ConsoleColor.DarkGreen
			or ConsoleColor.DarkMagenta
			or ConsoleColor.DarkRed
			or ConsoleColor.DarkYellow;

	private int ResolveRenderWidth()
	{
		if (PreferredWidth is int preferred && preferred > 0)
		{
			return preferred;
		}

		if (ReplSessionIO.IsSessionActive && ReplSessionIO.WindowSize is { } size && size.Width > 0)
		{
			return size.Width;
		}

		try
		{
			var consoleWidth = Console.WindowWidth;
			if (consoleWidth > 0)
			{
				return consoleWidth;
			}
		}
		catch
		{
			// Console width can throw in redirected/non-interactive hosts.
		}

		return FallbackWidth > 0 ? FallbackWidth : 120;
	}
}
