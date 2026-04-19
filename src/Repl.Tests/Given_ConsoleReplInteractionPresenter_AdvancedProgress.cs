using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ConsoleReplInteractionPresenter_AdvancedProgress
{
	[TestMethod]
	[Description("Advanced progress mode emits OSC 9;4 sequences for the supported progress states while preserving text output.")]
	public async Task When_AdvancedProgressAlways_Then_PresenterEmitsOscSequences()
	{
		var harness = new TerminalHarness(cols: 80, rows: 12);
		var presenter = new ConsoleReplInteractionPresenter(
			new InteractionOptions { AdvancedProgressMode = AdvancedProgressMode.Always },
			new OutputOptions { AnsiMode = AnsiMode.Always });

		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);

		await presenter.PresentAsync(
			new ReplProgressEvent("Downloading", Percent: 42),
			CancellationToken.None);
		await presenter.PresentAsync(
			new ReplProgressEvent("Retrying", Percent: 60, State: ReplProgressState.Warning, Details: "Transient issue"),
			CancellationToken.None);
		await presenter.PresentAsync(
			new ReplProgressEvent("Failed", Percent: 80, State: ReplProgressState.Error, Details: "Permanent issue"),
			CancellationToken.None);
		await presenter.PresentAsync(
			new ReplProgressEvent("Waiting", State: ReplProgressState.Indeterminate, Details: "Remote side"),
			CancellationToken.None);
		await presenter.PresentAsync(
			new ReplProgressEvent(string.Empty, State: ReplProgressState.Clear),
			CancellationToken.None);

		harness.RawOutput.Should().Contain("\u001b]9;4;1;42\u0007");
		harness.RawOutput.Should().Contain("\u001b]9;4;4;60\u0007");
		harness.RawOutput.Should().Contain("\u001b]9;4;2;80\u0007");
		harness.RawOutput.Should().Contain("\u001b]9;4;3;0\u0007");
		harness.RawOutput.Should().Contain("\u001b]9;4;0;0\u0007");
		harness.RawOutput.Should().Contain("Downloading: 42%");
		harness.RawOutput.Should().Contain("Waiting: Remote side");
	}

	[TestMethod]
	[Description("Advanced progress mode can be disabled without suppressing the textual progress fallback.")]
	public async Task When_AdvancedProgressNever_Then_TextRendersWithoutOscSequence()
	{
		var harness = new TerminalHarness(cols: 80, rows: 12);
		var presenter = new ConsoleReplInteractionPresenter(
			new InteractionOptions { AdvancedProgressMode = AdvancedProgressMode.Never },
			new OutputOptions { AnsiMode = AnsiMode.Always });

		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);

		await presenter.PresentAsync(
			new ReplProgressEvent("Downloading", Percent: 42),
			CancellationToken.None);

		harness.RawOutput.Should().Contain("Downloading: 42%");
		harness.RawOutput.Should().NotContain("\u001b]9;4;");
	}

	[TestMethod]
	[Description("Auto mode stays silent for advanced progress when the terminal session is running under tmux.")]
	public async Task When_AdvancedProgressAuto_And_TmuxDetected_Then_TextRendersWithoutOscSequence()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", "/tmp/tmux-1000/default,123,0"),
			("TERM", "tmux-256color"),
			("WT_SESSION", null),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var presenter = new ConsoleReplInteractionPresenter(
			new InteractionOptions { AdvancedProgressMode = AdvancedProgressMode.Auto },
			new OutputOptions { AnsiMode = AnsiMode.Always });

		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);

		await presenter.PresentAsync(
			new ReplProgressEvent("Downloading", Percent: 42),
			CancellationToken.None);

		harness.RawOutput.Should().Contain("Downloading: 42%");
		harness.RawOutput.Should().NotContain("\u001b]9;4;");
	}

	[TestMethod]
	[Description("Auto mode emits advanced progress for known terminals such as Windows Terminal.")]
	public async Task When_AdvancedProgressAuto_And_WindowsTerminalDetected_Then_PresenterEmitsOscSequence()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var presenter = new ConsoleReplInteractionPresenter(
			new InteractionOptions { AdvancedProgressMode = AdvancedProgressMode.Auto },
			new OutputOptions { AnsiMode = AnsiMode.Always });

		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);

		await presenter.PresentAsync(
			new ReplProgressEvent("Downloading", Percent: 42),
			CancellationToken.None);

		harness.RawOutput.Should().Contain("\u001b]9;4;1;42\u0007");
	}

	private sealed class EnvironmentVariableScope : IDisposable
	{
		private readonly (string Name, string? PreviousValue)[] _previousValues;

		public EnvironmentVariableScope(params (string Name, string? Value)[] variables)
		{
			_previousValues = variables
				.Select(static variable => (variable.Name, Environment.GetEnvironmentVariable(variable.Name)))
				.ToArray();

			foreach (var (name, value) in variables)
			{
				Environment.SetEnvironmentVariable(name, value);
			}
		}

		public void Dispose()
		{
			foreach (var (name, previousValue) in _previousValues)
			{
				Environment.SetEnvironmentVariable(name, previousValue);
			}
		}
	}
}
