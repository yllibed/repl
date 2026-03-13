namespace Repl.Interaction;

/// <summary>
/// Handles interaction requests in a chain-of-responsibility pipeline.
/// Implementations pattern-match on the <see cref="InteractionRequest"/> type
/// and return <see cref="InteractionResult.Success"/> for requests they handle,
/// or <see cref="InteractionResult.Unhandled"/> to delegate to the next handler.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is walked in registration order. The built-in console handler
/// is always the final fallback — it handles all standard request types.
/// </para>
/// <para>
/// Third-party packages (e.g. Spectre.Console, Terminal.Gui, or GUI frameworks)
/// register their own handler via DI:
/// </para>
/// <code>
/// services.AddSingleton&lt;IReplInteractionHandler, SpectreInteractionHandler&gt;();
/// </code>
/// <para>
/// A handler implementation typically looks like:
/// </para>
/// <code>
/// public class SpectreInteractionHandler : IReplInteractionHandler
/// {
///     public async ValueTask&lt;InteractionResult&gt; TryHandleAsync(
///         InteractionRequest request, CancellationToken ct) =&gt; request switch
///     {
///         AskSecretRequest r =&gt; InteractionResult.Success(await HandleSecret(r, ct)),
///         AskChoiceRequest r =&gt; InteractionResult.Success(await HandleChoice(r, ct)),
///         _ =&gt; InteractionResult.Unhandled,
///     };
/// }
/// </code>
/// </remarks>
public interface IReplInteractionHandler
{
	/// <summary>
	/// Attempts to handle an interaction request.
	/// </summary>
	/// <param name="request">The interaction request.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>
	/// <see cref="InteractionResult.Success"/> with the result value if handled,
	/// or <see cref="InteractionResult.Unhandled"/> to delegate to the next handler.
	/// </returns>
	ValueTask<InteractionResult> TryHandleAsync(
		InteractionRequest request,
		CancellationToken cancellationToken);
}
