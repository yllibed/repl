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

	[TestMethod]
	[Description("An app whose name contains a dot is recognised through its extensionless executable: Path.GetFileNameWithoutExtension reads the last dot-separated part as an extension and would turn \"Acme.Tool\" into \"Acme\". .NET 11 reports the apphost, not the dll, as the command head, so this is the only way such an app passes.")]
	public void When_TheAppNameContainsADotAndTheHeadIsItsExecutable_Then_HostIsSupported()
	{
		ShellCompletionHostValidator.IsSupportedHostProcess(
				processPath: "/apps/Acme.Tool",
				entryAssemblyName: "Acme.Tool",
				commandHead: "/apps/Acme.Tool",
				parentProcessName: "bash")
			.Should()
			.BeTrue();
	}

	[TestMethod]
	[Description("A dotted app name is still recognised when a managed host runs its dll, the way .NET 10 reports the command head.")]
	public void When_TheAppNameContainsADotAndTheHeadIsItsDll_Then_HostIsSupported()
	{
		ShellCompletionHostValidator.IsSupportedHostProcess(
				processPath: "/usr/share/dotnet/dotnet",
				entryAssemblyName: "Acme.Tool",
				commandHead: "/apps/Acme.Tool.dll",
				parentProcessName: "bash")
			.Should()
			.BeTrue();
	}

	[TestMethod]
	[Description("Only an executable extension is dropped: an executable named Acme.Other is not the app Acme, whatever follows the first dot.")]
	public void When_TheExecutableNameOnlyStartsLikeTheApp_Then_HostIsNotSupported()
	{
		ShellCompletionHostValidator.IsSupportedHostProcess(
				processPath: "/apps/Acme.Other",
				entryAssemblyName: "Acme",
				commandHead: "/apps/Acme.Other",
				parentProcessName: "bash")
			.Should()
			.BeFalse();
	}

	[TestMethod]
	[Description("A dotnet installed by dotnetup lives in a user folder (%LOCALAPPDATA%\\dotnetup\\dotnet on Windows, ~/.local/share/dotnetup/dotnet on Linux, observed with dotnetup 0.3.0-preview); only executable file names are compared, so that folder changes nothing.")]
	public void When_TheManagedHostIsADotnetupInstall_Then_HostIsSupported()
	{
		var (processPath, commandHead) = OperatingSystem.IsWindows()
			? ("C:\\Users\\ada\\AppData\\Local\\dotnetup\\dotnet\\dotnet.exe", "C:\\apps\\CoreBasicsSample.dll")
			: ("/home/ada/.local/share/dotnetup/dotnet/dotnet", "/apps/CoreBasicsSample.dll");
		ShellCompletionHostValidator.IsSupportedHostProcess(
				processPath,
				entryAssemblyName: "CoreBasicsSample",
				commandHead: commandHead,
				parentProcessName: "pwsh")
			.Should()
			.BeTrue();
	}
}
