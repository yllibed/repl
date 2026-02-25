namespace Repl.Tests;

[TestClass]
public sealed class Given_ShellCompletionHostValidator
{
	[TestMethod]
	[Description("Regression guard: verifies host process is rejected when executable head does not match current app assembly name.")]
	public void When_ProcessPathHeadDiffersFromAppAssemblyName_Then_HostIsNotSupported()
	{
		ShellCompletionHostValidator.IsSupportedHostProcess(
				"C:\\Program Files\\dotnet\\dotnet.exe",
				entryAssemblyName: "CoreBasicsSample")
			.Should()
			.BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies native executable host process is accepted for completion setup generation.")]
	public void When_ProcessPathIsNativeExecutable_Then_HostIsSupported()
	{
		ShellCompletionHostValidator.IsSupportedHostProcess(
				"C:\\apps\\CoreBasicsSample.exe",
				entryAssemblyName: "CoreBasicsSample")
			.Should()
			.BeTrue();
	}
}
