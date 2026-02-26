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
				entryAssemblyName: "CoreBasicsSample",
				commandHead: "dotnet",
				parentProcessName: "dotnet")
			.Should()
			.BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies native executable host process is accepted for completion setup generation.")]
	public void When_ProcessPathIsNativeExecutable_Then_HostIsSupported()
	{
		var executableName = OperatingSystem.IsWindows()
			? "CoreBasicsSample.exe"
			: "CoreBasicsSample";
		var processPath = Path.Combine(
			OperatingSystem.IsWindows() ? "C:\\" : "/",
			"apps",
			executableName);
		ShellCompletionHostValidator.IsSupportedHostProcess(
				processPath,
				entryAssemblyName: "CoreBasicsSample",
				commandHead: processPath,
				parentProcessName: "pwsh")
			.Should()
			.BeTrue();
	}

	[TestMethod]
	[Description("Regression guard: verifies managed host process is accepted when command head resolves to the current app binary name.")]
	public void When_ProcessPathIsManagedHostAndCommandHeadMatchesApp_Then_HostIsSupported()
	{
		var commandHead = OperatingSystem.IsWindows()
			? "C:\\apps\\CoreBasicsSample.dll"
			: "/apps/CoreBasicsSample.dll";
		ShellCompletionHostValidator.IsSupportedHostProcess(
				processPath: "/usr/share/dotnet/dotnet",
				entryAssemblyName: "CoreBasicsSample",
				commandHead: commandHead,
				parentProcessName: "bash")
			.Should()
			.BeTrue();
	}

	[TestMethod]
	[Description("Regression guard: verifies managed host process is rejected when parent process is the same host launcher.")]
	public void When_ProcessPathIsManagedHostAndParentMatchesHost_Then_HostIsNotSupported()
	{
		var commandHead = OperatingSystem.IsWindows()
			? "C:\\apps\\CoreBasicsSample.dll"
			: "/apps/CoreBasicsSample.dll";
		ShellCompletionHostValidator.IsSupportedHostProcess(
				processPath: "/usr/share/dotnet/dotnet",
				entryAssemblyName: "CoreBasicsSample",
				commandHead: commandHead,
				parentProcessName: "dotnet")
			.Should()
			.BeFalse();
	}
}
