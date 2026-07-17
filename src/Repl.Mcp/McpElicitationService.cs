using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Repl.Mcp;

/// <summary>
/// Internal implementation of <see cref="IMcpElicitation"/> backed by a live <see cref="McpServer"/> session.
/// Each public method maps to a single-field MCP elicitation request.
/// </summary>
/// <remarks>
/// Future: multi-field elicitation (Option B) would add a builder-based overload or a richer
/// <c>ElicitAsync</c> method that accepts multiple fields. The internal helper
/// <see cref="ElicitSingleFieldAsync"/> is already structured to support that evolution —
/// a multi-field variant would build the <see cref="ElicitRequestParams.RequestSchema"/> with
/// multiple properties instead of one.
/// </remarks>
internal sealed class McpElicitationService(McpRequestServerAccessor servers) : IMcpElicitation
{
	private const string FieldName = "value";

	public bool IsSupported => servers.Effective?.ClientCapabilities?.Elicitation is not null;

	public async ValueTask<string?> ElicitTextAsync(
		string message,
		CancellationToken cancellationToken = default)
	{
		var result = await ElicitSingleFieldAsync(
			message,
			new ElicitRequestParams.StringSchema(),
			cancellationToken).ConfigureAwait(false);

		return TryGetContentValue(result?.Content, out var value)
			? value.GetString()
			: null;
	}

	public async ValueTask<bool?> ElicitBooleanAsync(
		string message,
		CancellationToken cancellationToken = default)
	{
		var result = await ElicitSingleFieldAsync(
			message,
			new ElicitRequestParams.BooleanSchema(),
			cancellationToken).ConfigureAwait(false);

		return TryGetContentValue(result?.Content, out var value)
			? value.GetBoolean()
			: null;
	}

	public async ValueTask<int?> ElicitChoiceAsync(
		string message,
		IReadOnlyList<string> choices,
		CancellationToken cancellationToken = default)
	{
		var result = await ElicitSingleFieldAsync(
			message,
			new ElicitRequestParams.UntitledSingleSelectEnumSchema
			{
				Enum = choices.ToList(),
			},
			cancellationToken).ConfigureAwait(false);

		var selected = TryGetContentValue(result?.Content, out var value)
			? value.GetString()
			: null;
		if (selected is null)
		{
			return null;
		}

		var index = -1;
		for (var i = 0; i < choices.Count; i++)
		{
			if (string.Equals(choices[i], selected, StringComparison.OrdinalIgnoreCase))
			{
				index = i;
				break;
			}
		}

		return index >= 0 ? index : null;
	}

	public async ValueTask<double?> ElicitNumberAsync(
		string message,
		CancellationToken cancellationToken = default)
	{
		var result = await ElicitSingleFieldAsync(
			message,
			new ElicitRequestParams.NumberSchema(),
			cancellationToken).ConfigureAwait(false);

		return TryGetContentValue(result?.Content, out var value)
			? value.GetDouble()
			: null;
	}

	private async ValueTask<ElicitResult?> ElicitSingleFieldAsync(
		string message,
		ElicitRequestParams.PrimitiveSchemaDefinition schema,
		CancellationToken cancellationToken)
	{
		// Single read: the effective server must not change between the support check and
		// the call (a concurrent request re-binding the accessor must not be observed).
		if (servers.Effective is not { ClientCapabilities.Elicitation: not null } server)
		{
			return null;
		}

		var result = await server.ElicitAsync(
			new ElicitRequestParams
			{
				Message = message,
				RequestedSchema = new ElicitRequestParams.RequestSchema
				{
					Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
					{
						[FieldName] = schema,
					},
				},
			},
			cancellationToken).ConfigureAwait(false);

		if (result is not { IsAccepted: true })
		{
			return null;
		}

		return result;
	}

	private static bool TryGetContentValue(
		IDictionary<string, JsonElement>? content,
		out JsonElement value)
	{
		if (content?.TryGetValue(FieldName, out value) is true)
		{
			return true;
		}

		value = default;
		return false;
	}
}
