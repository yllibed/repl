namespace Repl;

/// <summary>
/// Declares an interactive answer slot that can be pre-filled via
/// <c>--answer:{name}=value</c> on the CLI or <c>answer:{name}</c> in MCP tool calls.
/// </summary>
/// <param name="Name">Answer name (matches the <c>name</c> parameter in <c>AskConfirmationAsync</c>, etc.).</param>
/// <param name="Type">Value type using route constraint names: <c>string</c>, <c>bool</c>, <c>int</c>, <c>guid</c>, <c>email</c>, etc.</param>
/// <param name="Description">Optional description for help text and agent tool schemas.</param>
public sealed record AnswerDeclaration(
	string Name,
	string Type = "string",
	string? Description = null);
