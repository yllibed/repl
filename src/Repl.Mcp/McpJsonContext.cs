using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Repl.Mcp;

/// <summary>
/// Source-generated JSON serialization context for trim-safe serialization.
/// </summary>
[JsonSerializable(typeof(JsonObject))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
