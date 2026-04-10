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
internal sealed class McpElicitationService : IMcpElicitation
{
	private const string FieldName = "value";

	private McpServer? _server;

	public bool IsSupported => _server?.ClientCapabilities?.Elicitation is not null;

	public async ValueTask<string?> ElicitTextAsync(
		string message,
		CancellationToken cancellationToken = default)
	{
		var result = await ElicitSingleFieldAsync(
			message,
			new ElicitRequestParams.StringSchema(),
			cancellationToken).ConfigureAwait(false);

		return result?.Content?[FieldName].GetString();
	}

	public async ValueTask<bool?> ElicitBooleanAsync(
		string message,
		CancellationToken cancellationToken = default)
	{
		var result = await ElicitSingleFieldAsync(
			message,
			new ElicitRequestParams.BooleanSchema(),
			cancellationToken).ConfigureAwait(false);

		return result?.Content?[FieldName].GetBoolean();
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

		var selected = result?.Content?[FieldName].GetString();
		if (selected is null)
		{
			return null;
		}

		var index = -1;
		for (var i = 0; i < choices.Count; i++)
		{
			if (string.Equals(choices[i], selected, StringComparison.Ordinal))
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

		return result?.Content?[FieldName].GetDouble();
	}

	internal void AttachServer(McpServer server) => _server = server;

	private async ValueTask<ElicitResult?> ElicitSingleFieldAsync(
		string message,
		ElicitRequestParams.PrimitiveSchemaDefinition schema,
		CancellationToken cancellationToken)
	{
		if (!IsSupported)
		{
			return null;
		}

		var result = await _server!.ElicitAsync(
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
}
