namespace Repl.Interaction;

/// <summary>
/// Requests a terminal screen clear.
/// </summary>
public sealed record ClearScreenRequest()
	: InteractionRequest<bool>("__clear_screen__", string.Empty);
