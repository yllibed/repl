namespace Repl;

public sealed partial class CoreReplApp
{
	private InteractiveSession? _interactiveSession;
	internal InteractiveSession Interactive => _interactiveSession ??= new(this);

	private bool ShouldEnterInteractive(GlobalInvocationOptions globalOptions, bool allowAuto) =>
		Interactive.ShouldEnterInteractive(globalOptions, allowAuto);

	private ValueTask<int> RunInteractiveSessionAsync(
		IReadOnlyList<string> initialScopeTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken) =>
		Interactive.RunInteractiveSessionAsync(initialScopeTokens, serviceProvider, cancellationToken);

	private string[] GetDeepestContextScopePath(IReadOnlyList<string> matchedPathTokens) =>
		Interactive.GetDeepestContextScopePath(matchedPathTokens);

	private ValueTask<AmbientCommandOutcome> TryHandleAmbientCommandAsync(
		List<string> inputTokens,
		List<string> scopeTokens,
		IServiceProvider serviceProvider,
		bool isInteractiveSession,
		CancellationToken cancellationToken) =>
		Interactive.TryHandleAmbientCommandAsync(inputTokens, scopeTokens, serviceProvider, isInteractiveSession, cancellationToken);

	private static ValueTask<AmbientCommandOutcome> HandleUpAmbientCommandAsync(
		List<string> scopeTokens,
		bool isInteractiveSession) =>
		InteractiveSession.HandleUpAmbientCommandAsync(scopeTokens, isInteractiveSession);

	private ValueTask<AmbientCommandOutcome> HandleExitAmbientCommandAsync() =>
		Interactive.HandleExitAmbientCommandAsync();

	private ValueTask<bool> HandleCompletionAmbientCommandAsync(
		IReadOnlyList<string> commandTokens,
		IReadOnlyList<string> scopeTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken) =>
		Interactive.HandleCompletionAmbientCommandAsync(commandTokens, scopeTokens, serviceProvider, cancellationToken);
}
