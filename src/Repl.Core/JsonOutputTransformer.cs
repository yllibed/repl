using System.Text.Json;

namespace Repl;

internal sealed class JsonOutputTransformer(JsonSerializerOptions serializerOptions) : IOutputTransformer
{
	public string Name => "json";

	public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable IL2026 // JSON object serialization is an explicit extensibility behavior in v1.
		var payload = JsonSerializer.Serialize(value, serializerOptions);
#pragma warning restore IL2026
		return ValueTask.FromResult(payload);
	}
}
