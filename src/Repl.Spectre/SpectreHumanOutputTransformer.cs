using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Repl.Spectre;

/// <summary>
/// Output transformer that renders values using light Spectre.Console layouts.
/// </summary>
internal sealed class SpectreHumanOutputTransformer : IOutputTransformer
{
	private readonly Func<HumanRenderSettings> _resolveRenderSettings;

	public SpectreHumanOutputTransformer()
		: this(DefaultResolveRenderSettings)
	{
	}

	public SpectreHumanOutputTransformer(Func<HumanRenderSettings> resolveRenderSettings)
	{
		ArgumentNullException.ThrowIfNull(resolveRenderSettings);
		_resolveRenderSettings = resolveRenderSettings;
	}

	/// <inheritdoc />
	public string Name => "spectre";

	/// <inheritdoc />
	public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (value is null)
		{
			return ValueTask.FromResult(string.Empty);
		}

		return ValueTask.FromResult(value switch
		{
			HelpRenderDocument help => RenderHelp(help),
			IReplResult replResult => RenderReplResult(replResult),
			string text => text,
			System.Collections.IEnumerable enumerable => RenderEnumerable(enumerable),
			_ when TryRenderObject(value, out var objectText) => objectText,
			_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
		});
	}

	private string RenderHelp(HelpRenderDocument help)
	{
		if (help.IsCommandHelp)
		{
			return help.Commands.Count == 1
				? RenderSingleCommandHelp(help.Commands[0])
				: RenderCommandList(help.Commands, "Commands");
		}

		var sections = new List<IRenderable>();
		if (help.Commands.Count > 0)
		{
			sections.Add(new Markup("[bold]Commands[/]"));
			sections.Add(BuildCommandsTable(help.Commands));
		}

		if (help.Scopes.Count > 0)
		{
			AppendSpacer(sections);
			sections.Add(new Markup("[bold]Scopes[/]"));
			sections.Add(BuildEntryTable(help.Scopes));
		}

		if (help.GlobalOptions.Count > 0)
		{
			AppendSpacer(sections);
			sections.Add(new Markup("[bold]Global Options[/]"));
			sections.Add(BuildEntryTable(help.GlobalOptions));
		}

		if (help.GlobalCommands.Count > 0)
		{
			AppendSpacer(sections);
			sections.Add(new Markup("[bold]Global Commands[/]"));
			sections.Add(BuildEntryTable(help.GlobalCommands));
		}

		return RenderToString(new Rows(sections));
	}

	private string RenderSingleCommandHelp(HelpRenderCommand command)
	{
		var sections = new List<IRenderable>
		{
			BuildLabelValueGrid(
				("Usage", command.Usage),
				("Description", command.Description)),
		};

		if (command.Aliases.Count > 0)
		{
			sections.Add(new Text(string.Empty));
			sections.Add(BuildLabelValueGrid(("Aliases", string.Join(", ", command.Aliases))));
		}

		if (command.Arguments.Count > 0)
		{
			AppendSpacer(sections);
			sections.Add(new Markup("[bold]Arguments[/]"));
			sections.Add(BuildEntryTable(command.Arguments));
		}

		if (command.Options.Count > 0)
		{
			AppendSpacer(sections);
			sections.Add(new Markup("[bold]Options[/]"));
			sections.Add(BuildEntryTable(command.Options));
		}

		if (command.Answers.Count > 0)
		{
			AppendSpacer(sections);
			sections.Add(new Markup("[bold]Answers[/]"));
			sections.Add(BuildEntryTable(command.Answers));
		}

		return RenderToString(new Rows(sections));
	}

	private string RenderCommandList(IReadOnlyList<HelpRenderCommand> commands, string title)
	{
		var sections = new List<IRenderable>
		{
			new Markup($"[bold]{Markup.Escape(title)}[/]"),
			BuildCommandsTable(commands),
		};
		return RenderToString(new Rows(sections));
	}

	private string RenderReplResult(IReplResult result)
	{
		var statusMarkup = result.Kind.ToLowerInvariant() switch
		{
			"text" => Markup.Escape(result.Message),
			"success" => $"[green]Success[/]: {Markup.Escape(result.Message)}",
			"error" => $"[red]Error[/]: {Markup.Escape(result.Message)}",
			"validation" => $"[yellow]Validation[/]: {Markup.Escape(result.Message)}",
			"not_found" => $"[yellow]Not found[/]: {Markup.Escape(result.Message)}",
			"cancelled" => $"[grey]Cancelled[/]: {Markup.Escape(result.Message)}",
			_ => $"[blue]Result[/]: {Markup.Escape(result.Message)}",
		};

		if (result.Details is null)
		{
			return RenderToString(new Markup(statusMarkup));
		}

		var details = RenderValueRenderable(result.Details, nested: false);
		return RenderToString(new Rows(new IRenderable[]
		{
			new Markup(statusMarkup),
			new Text(string.Empty),
			details,
		}));
	}

	private string RenderEnumerable(System.Collections.IEnumerable enumerable)
	{
		var items = enumerable.Cast<object?>().ToArray();
		if (items.Length == 0)
		{
			return "No results.";
		}

		var firstNonNull = items.FirstOrDefault(item => item is not null);
		if (firstNonNull is null)
		{
			return "No results.";
		}

		if (IsSimpleValue(firstNonNull.GetType()))
		{
			return string.Join(
				Environment.NewLine,
				items.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty));
		}

		var members = GetDisplayMembers(firstNonNull.GetType());
		if (members.Length == 0)
		{
			return string.Join(
				Environment.NewLine,
				items.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty));
		}

		return RenderToString(BuildObjectTable(items, members));
	}

	private bool TryRenderObject(object value, out string text)
	{
		var members = GetDisplayMembers(value.GetType());
		if (members.Length == 0)
		{
			text = string.Empty;
			return false;
		}

		text = RenderToString(BuildObjectGrid(value, members));
		return true;
	}

	private Grid BuildObjectGrid(object value, IReadOnlyList<DisplayMember> members)
	{
		var grid = new Grid();
		grid.AddColumn(new GridColumn().NoWrap().PadRight(2));
		grid.AddColumn(new GridColumn());

		foreach (var member in members)
		{
			var memberValue = member.Property.GetValue(value);
			grid.AddRow(
				new Markup($"[bold]{Markup.Escape(member.Label)}[/]:"),
				RenderValueRenderable(memberValue, nested: true, member));
		}

		return grid;
	}

	private static Table BuildObjectTable(object?[] items, IReadOnlyList<DisplayMember> members)
	{
		var table = new Table()
			.Border(TableBorder.None)
			.Collapse();

		foreach (var member in members)
		{
			table.AddColumn(new TableColumn($"[bold]{Markup.Escape(member.Label)}[/]"));
		}

		foreach (var item in items)
		{
			if (item is null)
			{
				table.AddRow(members.Select(_ => new Markup(string.Empty)).ToArray());
				continue;
			}

			var cells = new IRenderable[members.Count];
			for (var i = 0; i < members.Count; i++)
			{
				var memberValue = members[i].Property.GetValue(item);
				cells[i] = new Text(RenderInlineValue(memberValue, members[i]));
			}

			table.AddRow(cells);
		}

		return table;
	}

	private static Table BuildCommandsTable(IReadOnlyList<HelpRenderCommand> commands)
	{
		var table = new Table()
			.Border(TableBorder.None)
			.Collapse();
		table.AddColumn(new TableColumn("[bold]Command[/]"));
		table.AddColumn(new TableColumn("[bold]Description[/]"));

		foreach (var command in commands)
		{
			table.AddRow(
				new Text(command.Name),
				new Text(string.IsNullOrWhiteSpace(command.Description) ? "No description." : command.Description));
		}

		return table;
	}

	private static Table BuildEntryTable(IReadOnlyList<HelpRenderEntry> entries)
	{
		var table = new Table()
			.Border(TableBorder.None)
			.Collapse();
		table.AddColumn(new TableColumn("[bold]Name[/]"));
		table.AddColumn(new TableColumn("[bold]Description[/]"));

		foreach (var entry in entries)
		{
			table.AddRow(new Text(entry.Name), new Text(entry.Description));
		}

		return table;
	}

	private static Grid BuildLabelValueGrid(params (string Label, string Value)[] rows)
	{
		var grid = new Grid();
		grid.AddColumn(new GridColumn().NoWrap().PadRight(2));
		grid.AddColumn(new GridColumn());
		foreach (var row in rows)
		{
			grid.AddRow(
				new Markup($"[bold]{Markup.Escape(row.Label)}[/]:"),
				new Text(row.Value));
		}

		return grid;
	}

	private static void AppendSpacer(List<IRenderable> sections)
	{
		if (sections.Count > 0)
		{
			sections.Add(new Text(string.Empty));
		}
	}

	private IRenderable RenderValueRenderable(
		object? value,
		bool nested,
		DisplayMember? member = null)
	{
		if (value is null)
		{
			return new Text(member?.NullDisplayText ?? string.Empty);
		}

		if (value is string text)
		{
			return new Text(text);
		}

		if (value is System.Collections.IEnumerable enumerable)
		{
			var lines = RenderNestedEnumerableLines(enumerable);
			return lines.Count switch
			{
				0 => new Text(string.Empty),
				1 => new Text(lines[0]),
				_ => new Rows(lines.Select(line => (IRenderable)new Text(line))),
			};
		}

		if (nested && TryRenderInlineObject(value, out var inline))
		{
			return new Text(inline);
		}

		if (TryRenderObject(value, out var objectText))
		{
			return new Text(objectText);
		}

		return new Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
	}

	private static string RenderInlineValue(object? value, DisplayMember? member = null)
	{
		if (value is null)
		{
			return member?.NullDisplayText ?? string.Empty;
		}

		if (value is string text)
		{
			return text;
		}

		if (value is System.Collections.IEnumerable enumerable)
		{
			var lines = RenderNestedEnumerableLines(enumerable);
			return lines.Count == 0 ? string.Empty : string.Join("; ", lines);
		}

		if (TryRenderInlineObject(value, out var inline))
		{
			return inline;
		}

		return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
	}

	private static bool TryRenderInlineObject(object value, out string text)
	{
		var members = GetDisplayMembers(value.GetType());
		if (members.Length == 0)
		{
			text = string.Empty;
			return false;
		}

		text = string.Join(
			"; ",
			members.Select(member =>
			{
				var rendered = RenderInlineValue(member.Property.GetValue(value), member);
				return string.IsNullOrWhiteSpace(rendered)
					? string.Empty
					: $"{member.Label}: {rendered}";
			}).Where(rendered => !string.IsNullOrWhiteSpace(rendered)));
		return !string.IsNullOrWhiteSpace(text);
	}

	private static List<string> RenderNestedEnumerableLines(System.Collections.IEnumerable enumerable)
	{
		var items = enumerable.Cast<object?>().ToArray();
		if (items.Length == 0)
		{
			return [];
		}

		if (items.All(item => item is null))
		{
			return [];
		}

		var lines = new List<string>(items.Length);
		foreach (var item in items)
		{
			if (item is null)
			{
				continue;
			}

			var rendered = RenderInlineValue(item);
			if (!string.IsNullOrWhiteSpace(rendered))
			{
				lines.Add($"- {rendered}");
			}
		}

		return lines;
	}

	private string RenderToString(IRenderable renderable)
	{
#pragma warning disable MA0045
		using var writer = new StringWriter();
#pragma warning restore MA0045
		var console = SessionAnsiConsole.CreateForWriter(writer, ResolveRenderWidth());
		console.Write(renderable);
		return writer.ToString().TrimEnd();
	}

	private int ResolveRenderWidth() => _resolveRenderSettings().Width;

	private static HumanRenderSettings DefaultResolveRenderSettings() =>
		new(ResolveFallbackRenderWidth(), UseAnsi: false, Palette: new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark));

	private static int ResolveFallbackRenderWidth()
	{
		if (ReplSessionIO.WindowSize is { } size && size.Width > 0)
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
		catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException)
		{
			Trace.TraceInformation(
				"Could not resolve console width for Spectre rendering. {0}: {1}",
				ex.GetType().Name,
				ex.Message);
		}

		return 120;
	}

	private static bool IsSimpleValue(Type type) =>
		type.IsPrimitive
		|| type.IsEnum
		|| type == typeof(string)
		|| type == typeof(decimal)
		|| type == typeof(Guid)
		|| type == typeof(DateTime)
		|| type == typeof(DateTimeOffset)
		|| type == typeof(TimeSpan);

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2070",
		Justification = "Output rendering relies on runtime reflection over return models.")]
	private static DisplayMember[] GetDisplayMembers(Type type) =>
		type
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(property => property.GetMethod is not null && property.GetIndexParameters().Length == 0)
			.Select(property =>
			{
				var browsable = property.GetCustomAttribute<BrowsableAttribute>();
				if (browsable is not null && !browsable.Browsable)
				{
					return null;
				}

				var display = property.GetCustomAttribute<DisplayAttribute>();
				var displayFormat = property.GetCustomAttribute<DisplayFormatAttribute>();
				return new DisplayMember(
					property,
					string.IsNullOrWhiteSpace(display?.GetName()) ? property.Name : display!.GetName()!,
					display?.GetOrder(),
					displayFormat?.NullDisplayText);
			})
			.Where(member => member is not null)
			.Select(member => member!)
			.OrderBy(member => member.Order ?? int.MaxValue)
			.ThenBy(member => member.Property.MetadataToken)
			.ToArray();

	private sealed record DisplayMember(
		PropertyInfo Property,
		string Label,
		int? Order,
		string? NullDisplayText);
}
