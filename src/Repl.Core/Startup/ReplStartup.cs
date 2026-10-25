namespace Repl;

/// <summary>Entry point for preparation before application composition.</summary>
public static class ReplStartup
{
	/// <summary>Creates a preparation builder without constructing application services.</summary>
	/// <returns>A new builder.</returns>
	public static ReplStartupBuilder CreateBuilder() => new();
}
