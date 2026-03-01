namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_FileSystemParameterBinding
{
	[TestMethod]
	[Description("Regression guard: verifies FileInfo parameters bind from named options so handlers can receive file-path objects without manual conversion.")]
	public void When_FileInfoParameterIsBound_Then_HandlerReceivesExpectedPath()
	{
		var sut = ReplApp.Create();
		sut.Map("inspect", (FileInfo path) => path.FullName);
		var tempFile = Path.Combine(Path.GetTempPath(), $"repl-file-{Guid.NewGuid():N}.txt");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["inspect", "--path", tempFile, "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain(Path.GetFileName(tempFile));
	}

	[TestMethod]
	[Description("Regression guard: verifies DirectoryInfo parameters bind from named options so directory paths flow through handler typing consistently with other scalar conversions.")]
	public void When_DirectoryInfoParameterIsBound_Then_HandlerReceivesExpectedPath()
	{
		var sut = ReplApp.Create();
		sut.Map("inspect", (DirectoryInfo path) => path.FullName);
		var tempDirectory = Path.Combine(Path.GetTempPath(), $"repl-dir-{Guid.NewGuid():N}");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["inspect", "--path", tempDirectory, "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain(Path.GetFileName(tempDirectory));
	}
}
