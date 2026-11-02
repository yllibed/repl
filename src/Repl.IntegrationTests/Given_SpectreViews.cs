using System.Text;
using System.Text.Json;
using Repl.Spectre;
using Spectre.Console;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_SpectreViews
{
	[TestMethod]
	[Description("Registering a domain view leaves both root and command help on the standard Spectre help path.")]
	public void When_ViewsAreConfigured_Then_HelpRemainsBuiltIn()
	{
		var app = CreateApp();

		var root = ConsoleCaptureHelper.Capture(() => app.Run(["--help", "--no-logo"]));
		var command = ConsoleCaptureHelper.Capture(() => app.Run(["status", "--help", "--no-logo"]));

		root.ExitCode.Should().Be(0);
		root.Text.Should().Contain("Commands").And.Contain("status").And.NotContain("GeneratedAtUtc");
		command.ExitCode.Should().Be(0);
		command.Text.Should().Contain("Usage").And.Contain("Description").And.NotContain("State view");
	}

	[TestMethod]
	[Description("Selecting JSON bypasses the Spectre presentation and serializes the original record.")]
	public void When_JsonIsSelected_Then_TheOriginalDataIsSerialized()
	{
		var app = CreateApp();

		var output = ConsoleCaptureHelper.Capture(() => app.Run(["status", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		using var json = JsonDocument.Parse(output.Text);
		json.RootElement.GetProperty("ready").GetBoolean().Should().BeTrue();
	}

	[TestMethod]
	[Description("Custom views use the same sink-aware border transliteration and ANSI policy as native Spectre output.")]
	public void When_TheSessionOnlySupportsAscii_Then_CustomViewsRespectItsCapabilities()
	{
		var app = CreateApp();
		app.Options(options => options.Output.AnsiMode = AnsiMode.Never);
		var writer = new AsciiStringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null);
		ReplSessionIO.WindowSize = (60, 24);

		var exitCode = app.Run(["status", "--no-logo"]);

		exitCode.Should().Be(0);
		writer.ToString().Should().Contain("State view").And.Contain("+")
			.And.NotContain("╭").And.NotContain("┌").And.NotContain("\u001b[");
	}

	[TestMethod]
	[Description("NO_COLOR also disables styling inside a registered view on an ANSI-capable host.")]
	public void When_NoColorIsSet_Then_CustomViewsDoNotEmitAnsi()
	{
		using var environment = new EnvironmentVariableScope([("NO_COLOR", null), ("CLICOLOR_FORCE", "1"), ("TERM", null)]);
		var app = CreateApp();
		app.Options(options => options.Output.SetHostAnsiSupportResolver(static () => true));
		var baseline = ConsoleCaptureHelper.Capture(() => app.Run(["status", "--no-logo"]));
		using var noColor = new EnvironmentVariableScope([("NO_COLOR", "1")]);

		var output = ConsoleCaptureHelper.Capture(() => app.Run(["status", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		baseline.ExitCode.Should().Be(0);
		baseline.Text.Should().Contain("\u001b[", because: "the control run must actually enable styling");
		output.Text.Should().Contain("State view").And.NotContain("\u001b[");
	}

	[TestMethod]
	[DataRow(30)]
	[DataRow(120)]
	[Description("A custom layout is measured against the current session width, including narrow remote terminals.")]
	public void When_AViewExpands_Then_ItUsesTheSessionWidth(int width)
	{
		var app = ReplApp.Create().UseSpectreConsole(options =>
			options.AddView<Status>(_ => new Panel(new Text("State view")).Expand()));
		app.Options(options => options.Output.AnsiMode = AnsiMode.Never);
		app.Map("status", () => new Status(Ready: true));
		var writer = new AsciiStringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null);
		ReplSessionIO.WindowSize = (width, 24);

		var exitCode = app.Run(["status", "--no-logo"]);

		exitCode.Should().Be(0);
		writer.ToString().Should().Contain("State view");
		writer.ToString().Split(Environment.NewLine).Max(line => line.Length).Should().Be(width);
	}

	[TestMethod]
	[Description("A view's rounded border follows the application's Unicode option instead of creating its own console profile.")]
	public void When_UnicodeIsDisabled_Then_CustomViewsUseSafeBorders()
	{
		var app = ReplApp.Create().UseSpectreConsole(options =>
		{
			options.Unicode = false;
			options.AddView<Status>(_ => new Panel(new Text("State view")).RoundedBorder());
		});
		app.Map("status", () => new Status(Ready: true));
		var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null);
		ReplSessionIO.WindowSize = (60, 24);

		var exitCode = app.Run(["status", "--no-logo"]);

		exitCode.Should().Be(0);
		writer.ToString().Should().Contain("State view").And.Contain("┌").And.NotContain("╭");
	}

	private static ReplApp CreateApp()
	{
		var app = ReplApp.Create().UseSpectreConsole(options =>
			options.AddView<Status>(_ => new Panel(new Text("State view", new Style(Color.Red))).RoundedBorder()));
		app.Map("status", () => new Status(Ready: true)).WithDescription("Show current readiness.");
		return app;
	}

	private sealed record Status(bool Ready);
	private sealed class AsciiStringWriter : StringWriter
	{
		public override Encoding Encoding => Encoding.ASCII;
	}
}
