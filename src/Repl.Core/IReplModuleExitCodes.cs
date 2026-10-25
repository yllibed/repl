namespace Repl;

/// <summary>
/// A module that declares the exit codes of its own commands — for
/// example to follow another tool's conventions without changing the application's. The codes are read
/// once, when the module is mapped, and travel with the module wherever it is mounted.
/// </summary>
/// <example>
/// <code>
/// public sealed class TestRunnerModule : IReplModuleExitCodes
/// {
///     // dotnet test (Microsoft.Testing.Platform): 2 = tests failed, 5 = invalid command line.
///     public ReplExitCodeOverrides ExitCodes { get; } = new() { CompletedWithFailures = 2, UsageError = 5 };
///
///     public void Map(IReplMap map) => map.Map("tests run", RunTests);
/// }
/// </code>
/// </example>
public interface IReplModuleExitCodes : IReplModule
{
	/// <summary>Gets the codes this module overrides; see <see cref="ReplExitCodeOverrides"/>.</summary>
	ReplExitCodeOverrides ExitCodes { get; }
}
