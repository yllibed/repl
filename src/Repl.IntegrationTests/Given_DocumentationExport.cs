namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_DocumentationExport
{
	[TestMethod]
	[Description("Regression guard: verifies documentation export is enabled so that aggregate export excludes hidden commands.")]
	public void When_ExportingAggregateDocumentation_Then_HiddenCommandsAreExcluded()
	{
		var sut = ReplApp.Create()
			.UseDocumentationExport();
		sut.Map("contact list", () => "ok");
		sut.Map("contact debug dump-state", () => "hidden").Hidden();

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["doc", "export", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"path\": \"contact list\"");
		output.Text.Should().NotContain("contact debug dump-state");
	}

	[TestMethod]
	[Description("Regression guard: verifies aggregate documentation export excludes hidden contexts and their command trees.")]
	public void When_ExportingAggregateDocumentation_Then_HiddenContextsAreExcluded()
	{
		var sut = ReplApp.Create()
			.UseDocumentationExport();
		sut.Context("admin", admin =>
		{
			admin.Map("reset", () => "done");
		}).Hidden();
		sut.Map("status", () => "ok");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["doc", "export", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"path\": \"status\"");
		output.Text.Should().NotContain("\"path\": \"admin\"");
		output.Text.Should().NotContain("admin reset");
	}

	[TestMethod]
	[Description("Regression guard: verifies hidden command is explicitly targeted so that exact-path export includes hidden node.")]
	public void When_ExportingExactHiddenCommand_Then_HiddenCommandIsIncluded()
	{
		var sut = ReplApp.Create()
			.UseDocumentationExport();
		sut.Map("contact list", () => "ok");
		sut.Map("contact debug dump-state", () => "hidden").Hidden();

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["doc", "export", "contact", "debug", "dump-state", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"path\": \"contact debug dump-state\"");
		output.Text.Should().Contain("\"isHidden\": true");
	}

	[TestMethod]
	[Description("Regression guard: verifies hidden context is explicitly targeted so exact-path export includes the hidden context metadata.")]
	public void When_ExportingExactHiddenContext_Then_HiddenContextIsIncluded()
	{
		var sut = ReplApp.Create()
			.UseDocumentationExport();
		sut.Context("admin", admin =>
		{
			admin.Map("reset", () => "done");
		}).Hidden();

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["doc", "export", "admin", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"path\": \"admin\"");
		output.Text.Should().Contain("\"isHidden\": true");
		output.Text.Should().Contain("\"path\": \"admin reset\"");
	}

	[TestMethod]
	[Description("Regression guard: verifies markdown output is requested so that documentation export renders markdown.")]
	public void When_ExportingDocumentationInMarkdown_Then_MarkdownPayloadIsRendered()
	{
		var sut = ReplApp.Create()
			.UseDocumentationExport();
		sut.Map("contact list", () => "ok");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["doc", "export", "--markdown", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("# Overview");
		output.Text.Should().Contain("## Commands");
		output.Text.Should().Contain("`contact list`");
	}

	[TestMethod]
	[Description("Regression guard: verifies documentation export command is hidden by default so that help output does not advertise it.")]
	public void When_RequestingRootHelp_Then_DocumentationExportCommandIsHidden()
	{
		var sut = ReplApp.Create()
			.UseDocumentationExport();
		sut.Map("contact list", () => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("contact list");
		output.Text.Should().NotContain("doc export");
	}

	[TestMethod]
	[Description("Regression guard: verifies temporal route constraints are exported so that documentation surfaces typed route arguments.")]
	public void When_ExportingDocumentationAsJson_Then_TemporalConstraintTypesAreIncluded()
	{
		var sut = ReplApp.Create()
			.UseDocumentationExport();
		sut.Map("report {day:date} {duration:timespan}", (DateOnly day, TimeSpan duration) => $"{day}:{duration}");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["doc", "export", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"path\": \"report {day:date} {duration:timespan}\"");
		output.Text.Should().Contain("\"type\": \"date\"");
		output.Text.Should().Contain("\"type\": \"timespan\"");
	}
}
