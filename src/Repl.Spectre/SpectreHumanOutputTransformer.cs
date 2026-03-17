using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Repl.Spectre;

/// <summary>
/// Output transformer that renders values using Spectre.Console renderables
/// (bordered tables, panels, grids) for rich terminal output.
/// </summary>
internal sealed class SpectreHumanOutputTransformer : IOutputTransformer
{
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

		if (value is IReplResult replResult)
		{
			return ValueTask.FromResult(RenderReplResult(replResult));
		}

		if (value is string text)
		{
			return ValueTask.FromResult(text);
		}

		if (value is System.Collections.IEnumerable enumerable)
		{
			return ValueTask.FromResult(RenderEnumerable(enumerable));
		}

		if (TryRenderObject(value, out var objectText))
		{
			return ValueTask.FromResult(objectText);
		}

		return ValueTask.FromResult(
			Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
	}

	private static string RenderReplResult(IReplResult result)
	{
		var prefix = result.Kind.ToLowerInvariant() switch
		{
			"text" => string.Empty,
			"success" => "[green]Success[/]",
			"error" => "[red]Error[/]",
			"validation" => "[yellow]Validation[/]",
			"not_found" => "[yellow]Not found[/]",
			"cancelled" => "[grey]Cancelled[/]",
			_ => "[blue]Result[/]",
		};

		var message = string.IsNullOrWhiteSpace(prefix)
			? Markup.Escape(result.Message)
			: $"{prefix}: {Markup.Escape(result.Message)}";

		if (result.Details is null)
		{
			return RenderToString(new Markup(message));
		}

		var detailText = RenderValue(result.Details);
		if (string.IsNullOrWhiteSpace(detailText))
		{
			return RenderToString(new Markup(message));
		}

		return RenderToString(new Markup(message)) + Environment.NewLine + detailText;
	}

	private static string RenderEnumerable(System.Collections.IEnumerable enumerable)
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
				items.Select(item =>
					Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty));
		}

		var members = GetDisplayMembers(firstNonNull.GetType());
		if (members.Length == 0)
		{
			return string.Join(
				Environment.NewLine,
				items.Select(item =>
					Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty));
		}

		return RenderTable(items, members);
	}

	private static string RenderTable(object?[] items, DisplayMember[] members)
	{
		var table = new Table()
			.Border(TableBorder.Rounded)
			.BorderColor(Color.Grey);

		foreach (var member in members)
		{
			table.AddColumn(new TableColumn(Markup.Escape(member.Label))
				.NoWrap());
		}

		foreach (var item in items)
		{
			if (item is null)
			{
				table.AddRow(members.Select(_ => new Markup(string.Empty)).ToArray());
				continue;
			}

			var cells = new IRenderable[members.Length];
			for (var i = 0; i < members.Length; i++)
			{
				var memberValue = members[i].Property.GetValue(item);
				var text = RenderScalar(memberValue, members[i]);
				cells[i] = new Markup(Markup.Escape(text));
			}

			table.AddRow(cells);
		}

		return RenderToString(table);
	}

	private static bool TryRenderObject(object value, out string text)
	{
		var members = GetDisplayMembers(value.GetType());
		if (members.Length == 0)
		{
			text = string.Empty;
			return false;
		}

		var grid = new Grid();
		grid.AddColumn(new GridColumn().NoWrap().PadRight(2));
		grid.AddColumn(new GridColumn());

		foreach (var member in members)
		{
			var memberValue = member.Property.GetValue(value);
			if (memberValue is System.Collections.IEnumerable collectionValue
				&& memberValue is not string)
			{
				var rendered = RenderEnumerable(collectionValue);
				grid.AddRow(
					new Markup($"[bold]{Markup.Escape(member.Label)}[/]:"),
					new Markup(Markup.Escape(rendered)));
			}
			else
			{
				var rendered = RenderScalar(memberValue, member);
				grid.AddRow(
					new Markup($"[bold]{Markup.Escape(member.Label)}[/]:"),
					new Markup(Markup.Escape(rendered)));
			}
		}

		text = RenderToString(grid);
		return true;
	}

	private static string RenderScalar(object? value, DisplayMember? member)
	{
		if (value is null)
		{
			return member?.NullDisplayText ?? string.Empty;
		}

		if (value is string text)
		{
			return text;
		}

		return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
	}

	private static string RenderValue(object? value)
	{
		if (value is null)
		{
			return string.Empty;
		}

		if (value is string text)
		{
			return text;
		}

		if (value is System.Collections.IEnumerable enumerable)
		{
			return RenderEnumerable(enumerable);
		}

		if (TryRenderObject(value, out var objectText))
		{
			return objectText;
		}

		return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
	}

	private static string RenderToString(IRenderable renderable)
	{
		using var writer = new StringWriter();

		var width = 120;
		if (ReplSessionIO.WindowSize is { } size && size.Width > 0)
		{
			width = size.Width;
		}
		else
		{
			try
			{
				var consoleWidth = Console.WindowWidth;
				if (consoleWidth > 0)
				{
					width = consoleWidth;
				}
			}
			catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException)
			{
				// Width may be unavailable in redirected output.
			}
		}

		var console = SessionAnsiConsole.CreateForWriter(writer, width);
		console.Write(renderable);
		return writer.ToString().TrimEnd();
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
