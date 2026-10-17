using System.Threading.Channels;
using AwesomeAssertions;
using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
public sealed class Given_AdvancedInput_Editor
{
	[TestMethod]
	public async Task When_TabIsPressed_Then_TheEditorSubmitsNormalizedArguments()
	{
		var app = CreateApp(normalize: true);
		var line = await ReadEditedLineAsync(app, "/q \"hello world\"\t\r");
		InteractiveSession.TokenizeInteractiveInput(line!).Should().Equal("query", "hello world");
	}

	[TestMethod]
	public async Task When_NormalizationIsOptedOut_Then_TabPreservesLegacyText()
	{
		var app = CreateApp(normalize: false);
		(await ReadEditedLineAsync(app, "/q hello\t\r\r")).Should().StartWith("/q ");
	}

	[TestMethod]
	public async Task When_CursorHasAnOrigin_Then_NormalizationKeepsItsRelativePosition()
	{
		var app = CreateApp(normalize: true);
		var result = await app.Autocomplete.ResolveAutocompleteAsync(
			new("/q hello", 6, MenuRequested: false, ExplicitCompletion: true), [], app.CurrentServiceProvider, CancellationToken.None);
		result!.Value.NormalizedInput.Should().Be("query hello");
		result.Value.NormalizedCursor.Should().Be(9);
	}

	[TestMethod]
	public async Task When_AValueIsStillMissing_Then_LiveValidationIsIncomplete()
	{
		var app = CoreReplApp.Create();
		app.Map("query", (int limit) => limit);
		var input = await app.PreprocessInputAsync(["query", "--limit"], [], InputProcessingMode.Preview,
			"query --limit", 13, CancellationToken.None);
		var result = await app.AnalyzeInputAsync(input, app.CurrentServiceProvider, CancellationToken.None);
		result.Validation.State.Should().Be(InputValidationState.Incomplete);
	}

	[TestMethod]
	public async Task When_ACommandCannotMatchAnyRoute_Then_LiveValidationIsInvalid()
	{
		var app = CreateApp(normalize: true);
		var input = await app.PreprocessInputAsync(["unrecognized"], [], InputProcessingMode.Preview,
			"unrecognized", 12, CancellationToken.None);
		var result = await app.AnalyzeInputAsync(input, app.CurrentServiceProvider, CancellationToken.None);
		result.Validation.State.Should().Be(InputValidationState.Invalid);
	}

	[TestMethod]
	public async Task When_AnOldPreviewIgnoresCancellation_Then_TypingContinuesAndItsResultIsDiscarded()
	{
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var reader = new ControlledKeyReader();
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		var previousReader = ReplSessionIO.KeyReader;
		ReplSessionIO.KeyReader = reader;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		try
		{
			var reading = ConsoleLineReader.ReadLineAsync(history: null, async (request, _) =>
			{
				if (string.Equals(request.Input, "a", StringComparison.Ordinal))
				{
					firstStarted.TrySetResult();
#pragma warning disable VSTHRD003 // Deliberately hold an obsolete preview until newer input is processed.
					await releaseFirst.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
					return new(0, 0, [], HintLine: "obsolete diagnostic");
				}
				secondStarted.TrySetResult();
				return new(0, 0, [], HintLine: "current diagnostic");
			}, ConsoleLineReader.AutocompleteRenderMode.Rich, 8, Repl.Autocomplete.AutocompletePresentation.Hybrid,
			liveHintEnabled: true, colorizeInputLine: false, colorizeHintAndMenu: false,
			ConsoleLineReader.AutocompleteColorStyles.Empty, timeout.Token, backgroundInputAnalysis: true).AsTask();
			reader.Send('a');
			await firstStarted.Task.WaitAsync(timeout.Token);
			reader.Send('b');
			await secondStarted.Task.WaitAsync(timeout.Token);
			releaseFirst.TrySetResult();
			reader.Send('\r');
			(await reading).Line.Should().Be("ab");
			writer.ToString().Should().NotContain("obsolete diagnostic");
		}
		finally
		{
			releaseFirst.TrySetResult();
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	private static CoreReplApp CreateApp(bool normalize)
	{
		var app = CoreReplApp.Create();
		app.Options(options => options.Interactive.Autocomplete.NormalizeInputOnTab = normalize);
		app.UseArgumentPreprocessor((input, _) => ValueTask.FromResult(
			input.Arguments.Count != 0 && string.Equals(input.Arguments[0], "/q", StringComparison.Ordinal)
				? ArgumentPreprocessingResult.Replace(["query", .. input.Arguments.Skip(1)])
				: ArgumentPreprocessingResult.Continue()));
		app.Map("query {text}", (string text) => text);
		return app;
	}

	private static async Task<string?> ReadEditedLineAsync(CoreReplApp app, string input)
	{
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		var previous = ReplSessionIO.KeyReader;
		ReplSessionIO.KeyReader = new FakeKeyReader(input.Select(ToKey));
		try
		{
			var result = await ConsoleLineReader.ReadLineAsync(history: null,
				(request, token) => app.Autocomplete.ResolveAutocompleteAsync(request, [], app.CurrentServiceProvider, token),
				ConsoleLineReader.AutocompleteRenderMode.Rich, 8, Repl.Autocomplete.AutocompletePresentation.Hybrid,
				liveHintEnabled: true, colorizeInputLine: false, colorizeHintAndMenu: false,
				ConsoleLineReader.AutocompleteColorStyles.Empty, CancellationToken.None, backgroundInputAnalysis: true).ConfigureAwait(false);
			return result.Line;
		}
		finally { ReplSessionIO.KeyReader = previous; }
	}

	private static ConsoleKeyInfo ToKey(char value) => new(value,
		value == '\r' ? ConsoleKey.Enter : value == '\t' ? ConsoleKey.Tab : ConsoleKey.A,
		shift: false, alt: false, control: false);

	private sealed class ControlledKeyReader : IReplKeyReader
	{
		private readonly Channel<ConsoleKeyInfo> _keys = Channel.CreateUnbounded<ConsoleKeyInfo>();
		public bool KeyAvailable => _keys.Reader.TryPeek(out _);
		public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct) => _keys.Reader.ReadAsync(ct);
		public void Send(char value) => _keys.Writer.TryWrite(ToKey(value));
	}
}
