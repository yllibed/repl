namespace Repl.Testing;

/// <summary>
/// The platform whose signal-handling decisions a test wants to exercise, independent of the platform
/// it is running on. Declaring one lets a Windows wiring decision be asserted from Linux and the other
/// way round.
/// <para>
/// A declared platform changes decisions only. It never causes an operating-system registration to be
/// installed on that platform's behalf, and it never changes how a real signal aimed at the test
/// runner is treated — those are always evaluated against the actual host.
/// </para>
/// <para>
/// What a declared platform cannot buy: real delivery. No kernel will deliver a Windows console
/// control event on Linux, and whether a registration would succeed on a given host is a fact about
/// that host. Those need a spawned process on the matching platform.
/// </para>
/// </summary>
public sealed record ReplPlatformProfile
{
	/// <summary>
	/// The platform the test is actually running on, taken from <see cref="OperatingSystem"/>.
	/// </summary>
	public static ReplPlatformProfile Current { get; } = new()
	{
		IsWindows = OperatingSystem.IsWindows(),
		IsAndroid = OperatingSystem.IsAndroid(),
		IsBrowser = OperatingSystem.IsBrowser(),
		IsIOSOrMacCatalyst = OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst(),
		IsTvOS = OperatingSystem.IsTvOS(),
	};

	/// <summary>
	/// Windows: Ctrl+Break counts as a signal, and no SIGTERM registration is installed because the
	/// console keys are already owned by the cancel-key path.
	/// </summary>
	public static ReplPlatformProfile Windows { get; } = new() { IsWindows = true };

	/// <summary>
	/// Linux or macOS: Ctrl+Break is not a signal, and SIGTERM is registered. Both make the same
	/// decisions here, so they share one profile rather than pretending to differ.
	/// </summary>
	public static ReplPlatformProfile Unix { get; } = new();

	/// <summary>Android, where the signal bridge is unsupported and handling stays caller-owned.</summary>
	public static ReplPlatformProfile Android { get; } = new() { IsAndroid = true };

	/// <summary>WebAssembly in a browser, where the signal bridge is unsupported.</summary>
	public static ReplPlatformProfile Browser { get; } = new() { IsBrowser = true };

	/// <summary>iOS or Mac Catalyst, where the signal bridge is unsupported.</summary>
	public static ReplPlatformProfile IOS { get; } = new() { IsIOSOrMacCatalyst = true };

	/// <summary>tvOS, where the signal bridge is unsupported.</summary>
	public static ReplPlatformProfile TvOS { get; } = new() { IsTvOS = true };

	/// <summary>Whether the declared platform is Windows.</summary>
	public bool IsWindows { get; init; }

	/// <summary>Whether the declared platform is Android.</summary>
	public bool IsAndroid { get; init; }

	/// <summary>Whether the declared platform is WebAssembly in a browser.</summary>
	public bool IsBrowser { get; init; }

	/// <summary>Whether the declared platform is iOS or Mac Catalyst.</summary>
	public bool IsIOSOrMacCatalyst { get; init; }

	/// <summary>Whether the declared platform is tvOS.</summary>
	public bool IsTvOS { get; init; }

	/// <summary>
	/// Whether the signal bridge is available at all on the declared platform. When it is not,
	/// automatic handling degrades to caller-owned and says so once through the run's diagnostics.
	/// </summary>
	public bool IsSignalBridgeSupported =>
		ProcessSignalCoordinator.IsSignalBridgeSupportedForTesting(
			IsAndroid,
			IsBrowser,
			IsIOSOrMacCatalyst,
			IsTvOS);

	internal ProcessSignalCoordinator.SignalRegistrationPolicy ToPolicy(bool createRealRegistrations) =>
		new()
		{
			IsWindows = IsWindows,
			IsAndroid = IsAndroid,
			IsBrowser = IsBrowser,
			IsIOSOrMacCatalyst = IsIOSOrMacCatalyst,
			IsTvOS = IsTvOS,
			CreateRealRegistrations = createRealRegistrations,
		};
}
