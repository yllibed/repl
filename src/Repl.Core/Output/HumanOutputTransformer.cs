using System.Globalization;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

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
		return ValueTask.FromResult(Render(value, _resolveRenderSettings()).Text);
	}

	public ValueTask<RenderedPayload> RenderAsync(object? value, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult(Render(value, _resolveRenderSettings()));
	}

	public ValueTask<RenderedPayload> RenderPageAsync(IReplPage page, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(page);
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult(RenderPageBody(page, _resolveRenderSettings()));
	}

	private static RenderedPayload Render(object? value, HumanRenderSettings settings) => value switch
	{
		null => RenderedPayload.Plain(string.Empty),
		IReplPage page => RenderPageWithFooter(page, settings),
		IReplResult replResult => RenderReplResult(replResult, settings),
		string text => RenderedPayload.Plain(text),
		// Before the enumerable arm: a JsonObject enumerates as key/value pairs, and reflecting over those would
		// show JsonNode's CLR members instead of the data.
		_ when JsonHumanShape.TryGetNode(value, out var node) => RenderJson(node, settings),
		System.Collections.IEnumerable enumerable => RenderTopLevelEnumerable(enumerable, settings),
		_ when TryRenderObject(value, settings, out var objectText) => RenderedPayload.Plain(objectText),
		_ => RenderedPayload.Plain(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
	};

	private static RenderedPayload RenderPageWithFooter(IReplPage page, HumanRenderSettings settings) =>
		RenderPageBody(page, settings).WithFooterLine(ResultFlowPageFooterBuilder.RenderHuman(page));

	private static RenderedPayload RenderPageBody(IReplPage page, HumanRenderSettings settings)
	{
		if (page.UntypedItems.Count == 0)
		{
			return RenderedPayload.Plain("No results.");
		}

		// By its declared item type: a page of JSON nulls has no item to be recognized by.
		if (JsonHumanShape.IsJsonType(page.ItemType))
		{
			return RenderJsonItems(page.UntypedItems, settings);
		}

		return RenderItems([.. page.UntypedItems], depth: 0, settings);
	}

	private static RenderedPayload RenderTopLevelEnumerable(
		System.Collections.IEnumerable enumerable,
		HumanRenderSettings settings)
	{
		var lines = enumerable
			.Cast<object?>()
			.ToArray();
		if (lines.Length == 0)
		{
			return RenderedPayload.Plain("No results.");
		}

		if (TryRenderTable(lines, settings, out var table))
		{
			return table;
		}

		var scalarLines = lines
			.Select(item => RenderScalar(item, member: null, depth: 0, compactCollection: false, settings.Width, settings))
			.Where(item => !string.IsNullOrWhiteSpace(item))
			.ToArray();

		return RenderedPayload.Plain(scalarLines.Length == 0 ? "No results." : string.Join(Environment.NewLine, scalarLines));
	}

	private static RenderedPayload RenderJson(JsonNode? node, HumanRenderSettings settings) => node switch
	{
		JsonObject jsonObject => RenderedPayload.Plain(RenderJsonObject(jsonObject, settings)),
		JsonArray { Count: 0 } => RenderedPayload.Plain("No results."),
		// Its own path rather than the generic collection one, which recognizes JSON by its first non-null
		// item and so has nothing to go on for an array of nulls.
		JsonArray jsonArray => RenderJsonItems([.. jsonArray], settings),
		_ => RenderedPayload.Plain(JsonHumanShape.Literal(node)),
	};

	// The table's columns come from its own rows' keys rather than from a type, so a later page can name others;
	// declaring those keys lets the pager tell a repeated header from one it must keep.
	private static RenderedPayload RenderJsonItems(IReadOnlyList<object?> items, HumanRenderSettings settings)
	{
		if (!JsonHumanShape.TryGetObjectRows(items, out var columns, out var rows))
		{
			var literals = string.Join(
				Environment.NewLine,
				items.Select(item => JsonHumanShape.TryLiteral(item, out var literal)
					? literal
					: RenderScalar(item, member: null, depth: 0, compactCollection: true, settings.Width, settings)));
			return RenderedPayload.Plain(literals);
		}

		var tableRows = new List<string[]>(rows.Length + 1) { columns.Select(JsonHumanShape.Label).ToArray() };
		tableRows.AddRange(rows.Select(row => columns.Select(column => JsonHumanShape.Cell(row, column)).ToArray()));
		return FormatTable(tableRows, settings, columns);
	}

	private static string RenderJsonObject(JsonObject jsonObject, HumanRenderSettings settings)
	{
		if (jsonObject.Count == 0)
		{
			return "{}";
		}

		var entries = new List<RenderedEntry>(jsonObject.Count);
		foreach (var property in jsonObject)
		{
			entries.Add(new RenderedEntry(
				JsonHumanShape.Label(property.Key),
				JsonHumanShape.Literal(property.Value),
				IsMultiline: false));
		}

		return RenderEntries(entries, settings);
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
			if (JsonHumanShape.TryGetNode(memberValue, out var jsonValue))
			{
				entries.Add(new RenderedEntry(member.Label, JsonHumanShape.Literal(jsonValue), IsMultiline: false));
				continue;
			}

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
		HumanRenderSettings settings) =>
		RenderItems([.. collection.Cast<object?>()], depth, settings).Text;

	private static RenderedPayload RenderItems(object?[] values, int depth, HumanRenderSettings settings)
	{
		if (values.Length == 0)
		{
			return RenderedPayload.Plain(string.Empty);
		}

		if (TryRenderTable(values, settings, out var table))
		{
			return table;
		}

		var list = string.Join(
			Environment.NewLine,
			values.Select(value => $"- {RenderScalar(value, member: null, depth, compactCollection: false, settings.Width, settings)}"));
		return RenderedPayload.Plain(list);
	}

	private static bool TryRenderTable(
		object?[] values,
		HumanRenderSettings settings,
		[NotNullWhen(true)] out RenderedPayload? rendered)
	{
		var firstNonNull = values.FirstOrDefault(value => value is not null);
		if (firstNonNull is null)
		{
			rendered = null;
			return false;
		}

		// Only once the first item is JSON: ordinary collections must not pay for the JSON row scan.
		if (JsonHumanShape.IsJson(firstNonNull))
		{
			rendered = RenderJsonItems(values, settings);
			return true;
		}

		if (IsSimpleValue(firstNonNull.GetType()))
		{
			var lines = string.Join(
				Environment.NewLine,
				values.Select(value => RenderScalar(value, member: null, depth: 0, compactCollection: true, settings.Width, settings)));
			rendered = RenderedPayload.Plain(lines);
			return true;
		}

		var members = GetDisplayMembers(firstNonNull.GetType());
		if (members.Length == 0)
		{
			rendered = null;
			return false;
		}

		var rows = BuildTableRows(values, members, settings);
		rendered = FormatTable(rows, settings, columns: rows[0]);
		return true;
	}

	// The first row is the header. TextTableFormatter writes it on one line (it truncates a cell, never wraps it)
	// and, without ANSI styling to set it apart, a separator line under it: the layout declares exactly that.
	private static RenderedPayload FormatTable(
		List<string[]> rows,
		HumanRenderSettings settings,
		IReadOnlyList<string> columns)
	{
		var separated = !settings.UseAnsi;
		var text = TextTableFormatter.FormatRows(
			rows,
			settings.Width,
			includeHeaderSeparator: separated,
			separated ? TextTableStyle.None : TextTableStyle.ForHeader(settings.Palette.TableHeaderStyle));
		return new RenderedPayload(text, new RenderedLayout(separated ? 2 : 1, columns));
	}

	private static List<string[]> BuildTableRows(
		object?[] values,
		DisplayMember[] members,
		HumanRenderSettings settings)
	{
		var rows = new List<string[]>(values.Length + 1) { members.Select(member => member.Label).ToArray() };

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

		if (JsonHumanShape.TryGetNode(value, out var node))
		{
			return JsonHumanShape.Literal(node);
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
					TextTableFormatter.ToSingleLine(
						string.IsNullOrWhiteSpace(display?.GetName()) ? property.Name : display!.GetName()!),
					display?.GetOrder(),
					displayFormat?.NullDisplayText ?? JsonHumanShape.NullText(property.PropertyType));
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

	private static RenderedPayload RenderReplResult(IReplResult result, HumanRenderSettings settings)
	{
		// The message comes first, so no header starts the payload; a page's footer still ends it.
		if (result.Details is IReplPage page)
		{
			var rendered = RenderPageWithFooter(page, settings);
			return new RenderedPayload(
				$"{DescribeResult(result)}{Environment.NewLine}{rendered.Text}",
				RenderedLayout.None.WithFooter(rendered.Layout.FooterLineCount));
		}

		return RenderedPayload.Plain(RenderReplResultText(result, settings));
	}

	private static string DescribeResult(IReplResult result)
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

		return string.IsNullOrWhiteSpace(prefix)
			? result.Message
			: $"{prefix}: {result.Message}";
	}

	private static string RenderReplResultText(IReplResult result, HumanRenderSettings settings)
	{
		var message = DescribeResult(result);
		if (result.Details is null)
		{
			return message;
		}

		if (JsonHumanShape.TryGetNode(result.Details, out var jsonDetails))
		{
			return $"{message}{Environment.NewLine}{RenderJson(jsonDetails, settings).Text}";
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
