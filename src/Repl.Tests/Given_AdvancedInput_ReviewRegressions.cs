using System.Text.Json;

namespace Repl.Tests;

[TestClass]
public sealed class Given_AdvancedInput_ReviewRegressions
{
	[TestMethod]
	[Description("A CLI route named help is an ordinary command, so callbacks must not suppress its external actions as help rendering.")]
	public async Task When_CliHelpIsARoute_Then_ItHasCommandPurpose()
	{
		var app = CoreReplApp.Create();
		var purposes = new List<InputPurpose>();
		app.UseArgumentPreprocessor((input, _) =>
		{
			purposes.Add(input.Purpose);
			return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
		});
		app.UseInputValidator((ParsedInputContext input) =>
		{
			purposes.Add(input.Input.Purpose);
			return InputValidationResult.Valid();
		});
		app.Map("help", () => "ordinary command");
		(await app.RunAsync(["help"])).Should().Be(0);
		purposes.Should().Equal(InputPurpose.Command, InputPurpose.Command);
	}

	[TestMethod]
	[Description("The interactive question-mark alias must be classified as help before application callbacks decide whether to perform external actions.")]
	public async Task When_QuestionMarkIsSubmitted_Then_CallbacksSeeHelp()
	{
		var app = CoreReplApp.Create();
		var purposes = new List<InputPurpose>();
		app.Map("ping", () => "pong");
		app.UseArgumentPreprocessor((input, _) =>
		{
			if (input.Arguments is ["?"]) { purposes.Add(input.Purpose); }
			return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
		});
		app.UseInputValidator((ParsedInputContext input) =>
		{
			if (input.Input.Arguments is ["?"]) { purposes.Add(input.Input.Purpose); }
			return InputValidationResult.Valid();
		});
		using var writer = new StringWriter();
		using var reader = new StringReader("?\nexit\n");
		using var io = ReplSessionIO.SetSession(writer, reader);
		(await app.RunAsync([])).Should().Be(0);
		purposes.Should().Equal(InputPurpose.Help, InputPurpose.Help);
	}

	[TestMethod]
	[DataRow("validator")]
	[DataRow("help")]
	[DataRow("adapter")]
	[DataRow("rewrite")]
	[Description("Every early input refusal must use the output format of the effective invocation, including help and adapted input.")]
	public async Task When_InputIsRefused_Then_JsonFormatIsPreserved(string path)
	{
		var app = CoreReplApp.Create();
		app.Map("query", () => "must not run");
		if (path is "adapter" or "rewrite")
		{
			app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(path is "adapter"
				? ArgumentPreprocessingResult.Continue(InputValidationResult.Invalid("rejected input"))
				: ArgumentPreprocessingResult.Replace(["query", "--json"], validation: InputValidationResult.Invalid("rejected input"))));
		}
		else { app.UseInputValidator((ParsedInputContext _) => InputValidationResult.Invalid("rejected input")); }
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		string[] args = path is "rewrite" ? ["/old"] : path is "help" ? ["query", "--help", "--json"] : ["query", "--json"];
		(await app.RunAsync(args)).Should().Be(2);
		using var document = JsonDocument.Parse(writer.ToString());
		document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
		writer.ToString().Should().Contain("rejected input");
	}

	[TestMethod]
	[DataRow("exit", false)]
	[DataRow("history", true)]
	[DataRow("custom", false)]
	[DataRow("?", true)]
	[Description("Rejected ambient input must not dispatch or close the session, and a routed submission must still run its validator exactly once.")]
	public async Task When_AnAmbientIsRejected_Then_TheSessionContinues(string command, bool incomplete)
	{
		var app = CoreReplApp.Create();
		var rejectedCalls = 0;
		var routedCalls = 0;
		var customRan = false;
		var nextRan = false;
		app.Options(options => options.AmbientCommands.MapAmbient("custom", () => { customRan = true; }));
		app.Map("next", () => { nextRan = true; });
		app.UseInputValidator((ParsedInputContext input) =>
		{
			if (input.Input.Arguments is ["next"]) { routedCalls++; }
			if (input.Input.Arguments.Count != 0 && string.Equals(input.Input.Arguments[0], command, StringComparison.Ordinal) && rejectedCalls++ == 0)
			{
				return incomplete ? InputValidationResult.Incomplete("blocked ambient") : InputValidationResult.Invalid("blocked ambient");
			}
			return InputValidationResult.Valid();
		});
		using var writer = new StringWriter();
		using var reader = new StringReader($"{command}\nnext\nexit\n");
		using var io = ReplSessionIO.SetSession(writer, reader);
		(await app.RunAsync([])).Should().Be(0);
		nextRan.Should().BeTrue();
		customRan.Should().BeFalse();
		routedCalls.Should().Be(1);
		writer.ToString().Should().Contain("blocked ambient");
	}

	[TestMethod]
	[DataRow("completion", "__complete", true)]
	[DataRow("COMPLETION", "__COMPLETE", true)]
	[DataRow("complet", "__complete", false)]
	[Description("Only the actual hidden bridge route bypasses execution adapters; its non-routable abbreviation must remain adaptable as ordinary input.")]
	public async Task When_CompletionInputHasLeadingGlobals_Then_OnlyActualBridgesBypassAdapters(string setup, string protocol, bool bridge)
	{
		var app = CoreReplApp.Create();
		var modes = new List<InputProcessingMode>();
		app.UseArgumentPreprocessor((input, _) =>
		{
			modes.Add(input.Mode);
			return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
		});
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		await app.RunAsync(["--no-logo", setup, protocol, "--shell", "bash", "--line", "app q", "--cursor", "5"]);
		modes.Contains(InputProcessingMode.Execution).Should().Be(!bridge);
	}
}
