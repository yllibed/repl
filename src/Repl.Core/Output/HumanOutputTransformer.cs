using System.Globalization;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace Repl;

internal sealed class HumanOutputTransformer : IResultFlowOutputTransformer
{
	private readonly Func<HumanRenderSettings> _resolveRenderSettings;

	public HumanOutputTransformer()
		: this(DefaultResolveRenderSettings)
	{
	}

	public HumanOutputTransformer(Func<HumanRenderSettings> resolveRenderSettings)
	{
		ArgumentNullException.ThrowIfNull(resolveRenderSettings);
		_resolveRenderSettings = resolveRenderSettings;
	}

	public string Name => "human";

	public string MimeType => "text/plain";

	public bool SupportsInteractivePaging => true;

	public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var settings = _resolveRenderSettings();

		if (value is null)
		{
			return ValueTask.FromResult(string.Empty);
		}

		if (value is IReplPage page)
		{
			return ValueTask.FromResult(RenderPage(page, settings));
		}

		if (value is IReplResult replResult)
		{
			return ValueTask.FromResult(RenderReplResult(replResult, settings));
		}

		if (value is string text)
		{
			return ValueTask.FromResult(text);
		}

		if (value is System.Collections.IEnumerable enumerable)
		{
			var lines = enumerable
				.Cast<object?>()
				.ToArray();
			if (lines.Length == 0)
			{
				return ValueTask.FromResult("No results.");
			}

			if (TryRenderTable(lines, settings, includeHeader: true, out var tableText))
			{
				return ValueTask.FromResult(tableText);
			}

			var scalarLines = lines
				.Select(item => RenderScalar(item, member: null, depth: 0, compactCollection: false, settings.Width, settings))
				.Where(item => !string.IsNullOrWhiteSpace(item))
				.ToArray();

			if (scalarLines.Length == 0)
			{
				return ValueTask.FromResult("No results.");
			}

			return ValueTask.FromResult(string.Join(Environment.NewLine, scalarLines));
		}

		if (TryRenderObject(value, settings, out var objectText))
		{
			return ValueTask.FromResult(objectText);
		}

		return ValueTask.FromResult(
			Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
	}

	public ValueTask<string> TransformPageAsync(
		IReplPage page,
		ResultFlowPageRenderMode mode,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(page);
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult(RenderPage(page, _resolveRenderSettings(), mode));
	}

	private static string RenderPage(IReplPage page, HumanRenderSettings settings) =>
		RenderPage(page, settings, ResultFlowPageRenderMode.Initial, includeFooter: true);

	private static string RenderPage(IReplPage page, HumanRenderSettings settings, ResultFlowPageRenderMode mode) =>
		RenderPage(page, settings, mode, includeFooter: false);

	private static string RenderPage(
		IReplPage page,
		HumanRenderSettings settings,
		ResultFlowPageRenderMode mode,
		bool includeFooter)
	{
		var body = page.UntypedItems.Count == 0
			? "No results."
			: RenderCollection(
				page.UntypedItems,
				depth: 0,
				settings,
				includeTableHeader: mode == ResultFlowPageRenderMode.Initial);
		var footer = includeFooter ? ResultFlowPageFooterBuilder.RenderHuman(page) : string.Empty;
		return string.IsNullOrWhiteSpace(footer)
			? body
			: string.Concat(body, Environment.NewLine, footer);
	}

	private static bool TryRenderObject(object value, HumanRenderSettings settings, out string text)
	{
		var members = GetDisplayMembers(value.GetType());
		if (members.Length == 0)
		{
			text = string.Empty;
			return false;
		}

		var entries = new List<RenderedEntry>(members.Length);
		foreach (var member in members)
		{
			var memberValue = member.Property.GetValue(value);
			if (memberValue is System.Collections.IEnumerable collectionValue
				&& memberValue is not string)
			{
				var renderedCollection = RenderCollection(collectionValue, depth: 1, settings);
				if (string.IsNullOrWhiteSpace(renderedCollection))
				{
					entries.Add(new RenderedEntry(member.Label, "[]", IsMultiline: false));
					continue;
				}

				var indented = string.Join(
					Environment.NewLine,
					renderedCollection
						.Split(Environment.NewLine, StringSplitOptions.None)
						.Select(line => $"  {line}"));
				entries.Add(new RenderedEntry(member.Label, indented, IsMultiline: true));
				continue;
			}

			var rendered = RenderScalar(
				memberValue,
				member,
				depth: 0,
				compactCollection: false,
				settings.Width,
				settings);
			entries.Add(new RenderedEntry(member.Label, rendered, IsMultiline: false));
		}

		text = RenderEntries(entries, settings);
		return true;
	}

	private static string RenderCollection(
		System.Collections.IEnumerable collection,
		int depth,
		HumanRenderSettings settings,
		bool includeTableHeader = true)
	{
		var values = collection.Cast<object?>().ToArray();
		if (values.Length == 0)
		{
			return string.Empty;
		}

		if (TryRenderTable(values, settings, includeTableHeader, out var tableText))
		{
			return tableText;
		}

		return string.Join(
			Environment.NewLine,
			values.Select(value => $"- {RenderScalar(value, member: null, depth, compactCollection: false, settings.Width, settings)}"));
	}

	private static bool TryRenderTable(
		object?[] values,
		HumanRenderSettings settings,
		bool includeHeader,
		out string text)
	{
		var firstNonNull = values.FirstOrDefault(value => value is not null);
		if (firstNonNull is null)
		{
			text = string.Empty;
			return false;
		}

		if (IsSimpleValue(firstNonNull.GetType()))
		{
			text = string.Join(
				Environment.NewLine,
				values.Select(value => RenderScalar(value, member: null, depth: 0, compactCollection: true, settings.Width, settings)));
			return true;
		}

		var members = GetDisplayMembers(firstNonNull.GetType());
		if (members.Length == 0)
		{
			text = string.Empty;
			return false;
		}

		var rows = BuildTableRows(values, members, settings, includeHeader);
		var style = includeHeader && settings.UseAnsi
			? TextTableStyle.ForHeader(settings.Palette.TableHeaderStyle)
			: TextTableStyle.None;
		text = TextTableFormatter.FormatRows(
			rows,
			settings.Width,
			includeHeaderSeparator: includeHeader && !settings.UseAnsi,
			style);
		return true;
	}

	private static List<string[]> BuildTableRows(
		object?[] values,
		DisplayMember[] members,
		HumanRenderSettings settings,
		bool includeHeader)
	{
		var rows = new List<string[]>(values.Length + (includeHeader ? 1 : 0));
		if (includeHeader)
		{
			rows.Add(members.Select(member => member.Label).ToArray());
		}

		foreach (var item in values)
		{
			if (item is null)
			{
				rows.Add([.. members.Select(_ => string.Empty),]);
				continue;
			}

			rows.Add(
			[.. members.Select(member =>
				RenderScalar(
					member.Property.GetValue(item),
					member,
					depth: 0,
					compactCollection: true,
					settings.Width,
					settings)),
			]);
		}

		return rows;
	}

	private static HumanRenderSettings DefaultResolveRenderSettings()
	{
		var width = 120;

		if (ReplSessionIO.IsSessionActive && ReplSessionIO.WindowSize is { } size && size.Width > 0)
		{
			width = size.Width;
		}
		else
		{
			try
			{
				var resolvedWidth = Console.WindowWidth;
				if (resolvedWidth > 0)
				{
					width = resolvedWidth;
				}
			}
			catch
			{
				// Width may be unavailable in redirected output.
			}
		}

		return new HumanRenderSettings(width, UseAnsi: false, Palette: new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark));
	}

	private static string RenderScalar(
		object? value,
		DisplayMember? member,
		int depth,
		bool compactCollection,
		int renderWidth,
		HumanRenderSettings settings)
	{
		if (value is null)
		{
			return member?.NullDisplayText ?? string.Empty;
		}

		if (value is string text)
		{
			return text;
		}

		var valueType = value.GetType();
		if (IsSimpleValue(valueType))
		{
			return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
		}

		if (value is System.Collections.IEnumerable collection)
		{
			var values = collection.Cast<object?>().ToArray();
			if (compactCollection)
			{
				return values.Length.ToString(CultureInfo.InvariantCulture);
			}

			return RenderCollection(collection, depth + 1, settings);
		}

		if (depth > 0)
		{
			return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
		}

		var nestedMembers = GetDisplayMembers(valueType);
		if (nestedMembers.Length == 0)
		{
			return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
		}

		return string.Join(
			", ",
			nestedMembers.Select(nested =>
				RenderScalar(
					nested.Property.GetValue(value),
					nested,
					depth + 1,
					compactCollection: true,
					renderWidth,
					settings)));
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
		Justification = "Human rendering relies on runtime reflection over return models.")]
	private static DisplayMember[] GetDisplayMembers(
		Type type) =>
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

	private static string RenderReplResult(IReplResult result, HumanRenderSettings settings)
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

		if (result.Details is IReplPage page)
		{
			return $"{message}{Environment.NewLine}{RenderPage(page, settings)}";
		}

		if (TryRenderDictionary(result.Details, settings, out var dictionaryText))
		{
			return $"{message}{Environment.NewLine}{dictionaryText}";
		}

		if (TryRenderObject(result.Details, settings, out var details))
		{
			return $"{message}{Environment.NewLine}{details}";
		}

		var scalar = RenderScalar(result.Details, member: null, depth: 0, compactCollection: false, settings.Width, settings);
		if (string.IsNullOrWhiteSpace(scalar))
		{
			return message;
		}

		return $"{message}{Environment.NewLine}{scalar}";
	}

	private static bool TryRenderDictionary(object value, HumanRenderSettings settings, out string text)
	{
		if (value is not System.Collections.IDictionary dictionary)
		{
			text = string.Empty;
			return false;
		}

		var entries = new List<RenderedEntry>();
		foreach (System.Collections.DictionaryEntry entry in dictionary)
		{
			var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
			var itemValue = RenderScalar(entry.Value, member: null, depth: 0, compactCollection: true, settings.Width, settings);
			entries.Add(new RenderedEntry(key, itemValue, IsMultiline: false));
		}

		text = RenderEntries(entries, settings);
		return entries.Count > 0;
	}

	private static string RenderEntries(IReadOnlyList<RenderedEntry> entries, HumanRenderSettings settings)
	{
		if (entries.Count == 0)
		{
			return string.Empty;
		}

		var labelWidth = entries.Max(entry => entry.Label.Length);
		var lines = new List<string>(entries.Count * 2);
		foreach (var entry in entries)
		{
			var paddedLabel = entry.Label.PadRight(labelWidth);
			var renderedLabel = ApplyLabelStyle(paddedLabel, settings);
			if (!entry.IsMultiline)
			{
				lines.Add($"{renderedLabel}: {entry.Value}");
				continue;
			}

			lines.Add($"{renderedLabel}:");
			lines.Add(entry.Value);
		}

		return string.Join(Environment.NewLine, lines);
	}

	private static string ApplyLabelStyle(string label, HumanRenderSettings settings) =>
		settings.UseAnsi
			? AnsiText.Apply(label, settings.Palette.CommandStyle)
			: label;

	private sealed record RenderedEntry(string Label, string Value, bool IsMultiline);
}
