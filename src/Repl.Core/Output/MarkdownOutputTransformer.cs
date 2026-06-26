using System.Text;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace Repl;

internal sealed class MarkdownOutputTransformer : IOutputTransformer
{
	private static readonly string[] ObjectTableHeader = ["Field", "Value"];
	private static readonly string[] ObjectTableSeparator = ["---", "---"];

	public string Name => "markdown";

	public string MimeType => "text/markdown";

	public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (value is null)
		{
			return ValueTask.FromResult(string.Empty);
		}

		if (value is ReplDocumentationModel documentation)
		{
			return ValueTask.FromResult(RenderDocumentation(documentation));
		}

		if (value is HelpRenderDocument help)
		{
			return ValueTask.FromResult(RenderHelp(help));
		}

		if (value is string text)
		{
			return ValueTask.FromResult(text);
		}

		if (value is IReplPage page)
		{
			return ValueTask.FromResult(RenderPage(page));
		}

		if (value is IReplResult result)
		{
			return ValueTask.FromResult(RenderReplResult(result));
		}

		if (value is System.Collections.IEnumerable enumerable)
		{
			return ValueTask.FromResult(RenderEnumerable(enumerable));
		}

		return ValueTask.FromResult(RenderObject(value));
	}

	private static string RenderReplResult(IReplResult result)
	{
		var prefix = result.Kind.ToLowerInvariant() switch
		{
			"text" => string.Empty,
			"success" => "Success",
			"error" => "Error",
			"validation" => "Validation",
			"not_found" => "Not found",
			"cancelled" => "Cancelled",
			_ => "Result",
		};

		var message = string.IsNullOrWhiteSpace(prefix)
			? result.Message
			: $"{prefix}: {result.Message}";

		if (result.Details is null)
		{
			return message;
		}

		var details = result.Details is string detailsText
			? detailsText
			: result.Details is IReplPage page
				? RenderPage(page)
			: result.Details is System.Collections.IEnumerable enumerable && result.Details is not string
				? RenderEnumerable(enumerable)
				: RenderObject(result.Details);

		if (string.IsNullOrWhiteSpace(message))
		{
			return details;
		}

		return string.Concat(message, Environment.NewLine, Environment.NewLine, details);
	}

	private static string RenderPage(IReplPage page)
	{
		var body = page.UntypedItems.Count == 0
			? "No results."
			: RenderEnumerable(page.UntypedItems);
		var footer = ResultFlowPageFooterBuilder.RenderMarkdown(page);
		return string.IsNullOrWhiteSpace(footer)
			? body
			: string.Concat(body, Environment.NewLine, Environment.NewLine, footer);
	}

	private static string RenderEnumerable(System.Collections.IEnumerable enumerable)
	{
		var items = ToObjectArray(enumerable);
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
				items.Select(item => $"- {Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}"));
		}

		var members = GetDisplayMembers(firstNonNull.GetType());
		if (members.Length == 0)
		{
			return string.Join(
				Environment.NewLine,
				items.Select(item => $"- {item?.ToString() ?? string.Empty}"));
		}

		var emptyRow = new string[members.Length];
		Array.Fill(emptyRow, string.Empty);
		var rows = new List<string[]>(items.Length + 1)
		{
			members.Select(member => EscapeCell(member.Label)).ToArray(),
			members.Select(_ => "---").ToArray(),
		};
		foreach (var item in items)
		{
			if (item is null)
			{
				rows.Add(emptyRow);
				continue;
			}

			rows.Add(
				members.Select(member =>
				{
					var value = member.Property.GetValue(item);
					var text = value is null
						? member.NullDisplayText ?? string.Empty
						: Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
					return EscapeCell(text);
				}).ToArray());
		}

		return string.Join(Environment.NewLine, rows.Select(row => $"| {string.Join(" | ", row)} |"));
	}

	private static string RenderObject(object value)
	{
		var members = GetDisplayMembers(value.GetType());
		if (members.Length == 0)
		{
			return value.ToString() ?? string.Empty;
		}

		var rows = new List<string[]>(members.Length + 2)
		{
			ObjectTableHeader,
			ObjectTableSeparator,
		};
		foreach (var member in members)
		{
			var memberValue = member.Property.GetValue(value);
			rows.Add(
				new[]
				{
					EscapeCell(member.Label),
					EscapeCell(RenderScalar(memberValue, member)),
				});
		}

		return string.Join(Environment.NewLine, rows.Select(row => $"| {string.Join(" | ", row)} |"));
	}

	private static string RenderScalar(object? value, DisplayMember member)
	{
		if (value is null)
		{
			return member.NullDisplayText ?? string.Empty;
		}

		if (value is string text)
		{
			return text;
		}

		var type = value.GetType();
		if (IsSimpleValue(type))
		{
			return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
		}

		if (value is System.Collections.IEnumerable enumerable)
		{
			var count = CountEnumerable(enumerable);
			return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		var nestedMembers = GetDisplayMembers(type);
		if (nestedMembers.Length == 0)
		{
			return value.ToString() ?? string.Empty;
		}

		return string.Join(
			", ",
			nestedMembers.Select(nested =>
				Convert.ToString(nested.Property.GetValue(value), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
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

	private static string EscapeCell(string value) =>
		value.Replace("|", "\\|", StringComparison.Ordinal);

	private static object?[] ToObjectArray(System.Collections.IEnumerable enumerable)
	{
		if (enumerable is object?[] array)
		{
			return array;
		}

		if (enumerable is IReplPage page)
		{
			return page.UntypedItems as object?[] ?? [.. page.UntypedItems];
		}

		return enumerable.Cast<object?>().ToArray();
	}

	private static int CountEnumerable(System.Collections.IEnumerable enumerable)
	{
		if (enumerable is System.Collections.ICollection collection)
		{
			return collection.Count;
		}

		if (enumerable is IReadOnlyCollection<object?> readonlyCollection)
		{
			return readonlyCollection.Count;
		}

		return enumerable.Cast<object?>().Count();
	}

	private static string RenderHelp(HelpRenderDocument help)
	{
		var builder = new StringBuilder();
		if (help.IsCommandHelp)
		{
			if (help.Commands.Count == 1)
			{
				RenderCommandHelp(builder, help.Commands[0]);
			}
			else
			{
				AppendEntrySection(builder, "Commands", help.Commands.Select(CommandEntry).ToArray());
			}

			return builder.ToString().TrimEnd();
		}

		builder.AppendLine($"# Help: {EscapeMarkdown(help.Scope)}");
		AppendCommandSection(builder, help.Commands);
		AppendEntrySection(builder, "Scopes", help.Scopes);
		AppendEntrySection(builder, "Global Options", help.GlobalOptions);
		AppendEntrySection(builder, "Global Commands", help.GlobalCommands);
		return builder.ToString().TrimEnd();
	}

	private static void RenderCommandHelp(StringBuilder builder, HelpRenderCommand command)
	{
		builder.AppendLine($"# `{EscapeMarkdown(command.Usage)}`");
		builder.AppendLine();
		builder.AppendLine($"- **Usage**: `{EscapeMarkdown(command.Usage)}`");
		builder.AppendLine($"- **Description**: {EscapeMarkdown(command.Description)}");
		if (command.Aliases.Count > 0)
		{
			builder.AppendLine($"- **Aliases**: {EscapeMarkdown(string.Join(", ", command.Aliases))}");
		}

		AppendEntrySection(builder, "Arguments", command.Arguments);
		AppendEntrySection(builder, "Options", command.Options);
		AppendEntrySection(builder, "Result Flow", command.ResultFlow);
		AppendEntrySection(builder, "Answers", command.Answers);
	}

	private static void AppendCommandSection(StringBuilder builder, IReadOnlyList<HelpRenderCommand> commands)
	{
		if (commands.Count == 0)
		{
			return;
		}

		AppendEntrySection(builder, "Commands", commands.Select(CommandEntry).ToArray());
	}

	private static HelpRenderEntry CommandEntry(HelpRenderCommand command) =>
		new(command.Name, command.Description);

	private static void AppendEntrySection(
		StringBuilder builder,
		string title,
		IReadOnlyList<HelpRenderEntry> entries)
	{
		if (entries.Count == 0)
		{
			return;
		}

		builder.AppendLine();
		builder.AppendLine($"## {title}");
		builder.AppendLine();
		builder.AppendLine("| Name | Description |");
		builder.AppendLine("| --- | --- |");
		foreach (var entry in entries)
		{
			builder
				.Append("| `")
				.Append(EscapeCell(entry.Name))
				.Append("` | ")
				.Append(EscapeCell(entry.Description))
				.AppendLine(" |");
		}
	}

	private static string EscapeMarkdown(string value) =>
		value.Replace("|", "\\|", StringComparison.Ordinal);

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2070",
		Justification = "Markdown rendering reflects over runtime return models in the same way as human output.")]
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

	private sealed record DisplayMember(PropertyInfo Property, string Label, int? Order, string? NullDisplayText);

	private static string RenderDocumentation(ReplDocumentationModel model)
	{
		var builder = new StringBuilder();
		RenderHeader(builder, model.App);
		RenderContexts(builder, model.Contexts);
		RenderCommands(builder, model.Commands);
		return builder.ToString().TrimEnd();
	}

	private static void RenderHeader(StringBuilder builder, ReplDocApp app)
	{
		builder.AppendLine("# Overview");
		builder.AppendLine();
		builder.AppendLine($"- **Name**: {app.Name}");
		builder.AppendLine($"- **Version**: {app.Version ?? "-"}");
		builder.AppendLine($"- **Description**: {app.Description ?? "-"}");
		builder.AppendLine();
	}

	private static void RenderContexts(StringBuilder builder, IReadOnlyList<ReplDocContext> contexts)
	{
		builder.AppendLine("## Contexts");
		builder.AppendLine();
		if (contexts.Count == 0)
		{
			builder.AppendLine("No contexts.");
			builder.AppendLine();
			return;
		}

		foreach (var context in contexts)
		{
			builder.Append("- ").Append('`').Append(context.Path).Append('`');
			if (!string.IsNullOrWhiteSpace(context.Description))
			{
				builder.Append(" - ").Append(context.Description);
			}

			builder.AppendLine();
		}

		builder.AppendLine();
	}

	private static void RenderCommands(StringBuilder builder, IReadOnlyList<ReplDocCommand> commands)
	{
		builder.AppendLine("## Commands");
		builder.AppendLine();
		if (commands.Count == 0)
		{
			builder.AppendLine("No commands.");
			return;
		}

		foreach (var command in commands)
		{
			builder.AppendLine($"### `{command.Path}`");
			builder.AppendLine();
			builder.AppendLine($"- Description: {command.Description ?? "-"}");
			builder.AppendLine($"- Aliases: {(command.Aliases.Count == 0 ? "-" : string.Join(", ", command.Aliases))}");
			builder.AppendLine($"- Hidden: {command.IsHidden}");
			AppendArguments(builder, command.Arguments);
			AppendOptions(builder, command.Options);
			builder.AppendLine();
		}
	}

	private static void AppendArguments(StringBuilder builder, IReadOnlyList<ReplDocArgument> arguments)
	{
		if (arguments.Count == 0)
		{
			return;
		}

		builder.AppendLine("- Arguments:");
		foreach (var argument in arguments)
		{
			builder.Append("  - ").Append('`').Append(argument.Name).Append('`').Append(" (")
				.Append(argument.Type).Append(')');
			if (!argument.Required)
			{
				builder.Append(" *(optional)*");
			}

			if (!string.IsNullOrWhiteSpace(argument.Description))
			{
				builder.Append(" - ").Append(argument.Description);
			}

			builder.AppendLine();
		}
	}

	private static void AppendOptions(StringBuilder builder, IReadOnlyList<ReplDocOption> options)
	{
		if (options.Count == 0)
		{
			return;
		}

		builder.AppendLine("- Options:");
		foreach (var option in options)
		{
			builder.Append("  - `--").Append(option.Name).Append("` (")
				.Append(option.Type).Append(')');
			if (!string.IsNullOrWhiteSpace(option.Description))
			{
				builder.Append(" - ").Append(option.Description);
			}

			builder.AppendLine();
		}
	}
}
