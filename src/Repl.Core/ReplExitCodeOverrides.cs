namespace Repl;

/// <summary>
/// The exit codes a module declares for the outcomes of its own commands, through
/// <see cref="IReplModuleExitCodes"/>. A code left <see langword="null"/> is inherited from the module
/// that mapped this one, and then from <see cref="ReplOptions.ExitCodes"/>.
/// </summary>
/// <remarks>
/// Only the outcomes a command produces can be overridden. Help, cancellation, interruption and framework
/// errors describe the run rather than one module's command, and keep the application's codes; an
/// <see cref="IExitResult"/> keeps the code its handler chose.
/// </remarks>
public sealed record ReplExitCodeOverrides
{
	/// <summary>Gets the code for <see cref="ReplExecutionOutcomeKind.Success"/>.</summary>
	public int? Success { get; init; }

	/// <summary>
	/// Gets the code for <see cref="ReplExecutionOutcomeKind.UsageError"/>: an option, argument, output
	/// format or command the module's commands refused, including an unknown or ambiguous command under the
	/// module's own words.
	/// </summary>
	public int? UsageError { get; init; }

	/// <summary>Gets the code for <see cref="ReplExecutionOutcomeKind.BindingError"/>.</summary>
	public int? BindingError { get; init; }

	/// <summary>Gets the code for <see cref="ReplExecutionOutcomeKind.HandlerError"/>.</summary>
	public int? HandlerError { get; init; }

	/// <summary>Gets the code for <see cref="ReplExecutionOutcomeKind.HandlerException"/>.</summary>
	public int? HandlerException { get; init; }

	/// <summary>Gets the code for <see cref="ReplExecutionOutcomeKind.CompletedWithFailures"/>.</summary>
	public int? CompletedWithFailures { get; init; }

	/// <summary>The code for <paramref name="kind"/>, or <see langword="null"/> when it is not overridden.</summary>
	internal int? For(ReplExecutionOutcomeKind kind) =>
		kind switch
		{
			ReplExecutionOutcomeKind.Success => Success,
			ReplExecutionOutcomeKind.UsageError => UsageError,
			ReplExecutionOutcomeKind.BindingError => BindingError,
			ReplExecutionOutcomeKind.HandlerError => HandlerError,
			ReplExecutionOutcomeKind.HandlerException => HandlerException,
			ReplExecutionOutcomeKind.CompletedWithFailures => CompletedWithFailures,
			_ => null,
		};

	/// <summary>These codes, completed by those of <paramref name="parent"/> where they are unset.</summary>
	internal ReplExitCodeOverrides InheritFrom(ReplExitCodeOverrides? parent) =>
		parent is null
			? this
			: new ReplExitCodeOverrides
			{
				Success = Success ?? parent.Success,
				UsageError = UsageError ?? parent.UsageError,
				BindingError = BindingError ?? parent.BindingError,
				HandlerError = HandlerError ?? parent.HandlerError,
				HandlerException = HandlerException ?? parent.HandlerException,
				CompletedWithFailures = CompletedWithFailures ?? parent.CompletedWithFailures,
			};
}
