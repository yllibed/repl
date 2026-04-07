namespace Repl.Interaction;

/// <summary>
/// Base type for all interaction requests flowing through the handler pipeline.
/// </summary>
/// <param name="Name">Prompt name (used for prefill via <c>--answer:name=value</c>).</param>
/// <param name="Prompt">Prompt text displayed to the user.</param>
public abstract record InteractionRequest(string Name, string Prompt);

/// <summary>
/// Typed interaction request that declares the expected result type.
/// Derive from this to define new interaction controls.
/// </summary>
/// <typeparam name="TResult">The type returned by the interaction.</typeparam>
/// <param name="Name">Prompt name.</param>
/// <param name="Prompt">Prompt text.</param>
public abstract record InteractionRequest<TResult>(string Name, string Prompt)
	: InteractionRequest(Name, Prompt);
