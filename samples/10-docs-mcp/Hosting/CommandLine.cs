using System.Runtime.InteropServices;
using Repl;
using Repl.Parameters;
using Results = Repl.Results;

namespace DocsMcpSample.Hosting;

internal static class CommandLine
{
	internal static ReplApp Create(Settings settings)
	{
		var app = ReplApp.Create().UseCliProfile().WithDescription("Search your Markdown documents and administer one running index.");
		app.Options(options =>
		{
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.BannerEnabled = false;
			options.ExitCodes.Resolver = outcome =>
			{
				if (outcome.Kind != ReplExecutionOutcomeKind.Cancelled && outcome.Exception is { } error)
				{
					Console.Error.WriteLine(error);
				}
				return outcome.ExitCode;
			};
		});
		app.UseStartupHook((ReplStartupContext startup) =>
			startup.RemainingArguments.Count == 0 && startup.Purpose is not (InputPurpose.Help or InputPurpose.Completion)
				? ArgumentPreprocessingResult.Replace(["serve", .. startup.Arguments])
				: ArgumentPreprocessingResult.Continue());

		// The remote suffix is literal data: even --help and output options belong to the remote process.
		app.UseArgumentPreprocessor((input, _) => input.Arguments is ["exec", _, ..]
			? ArgumentPreprocessingResult.Replace(["exec"]) : ArgumentPreprocessingResult.Continue());
		app.UseInvocationHook((ReplInvocationContext invocation) =>
			invocation.Parsed.Command == "exec" && invocation.OriginalRequest.Arguments is ["exec", _, ..]
				? ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("exec",
					new Dictionary<string, object?>(StringComparer.Ordinal) { ["arguments"] = invocation.OriginalRequest.Arguments.Skip(1).ToArray() }))
				: ReplInvocationResult.Continue());

		app.Map("serve", async (CancellationToken ct) => Results.Exit(await Server.RunAsync(settings, ct)))
			.WithDescription("Start the index, public MCP endpoint and private administration server.");
		app.Map("console", async (CancellationToken ct) => Results.Exit(await ClientMode.ConsoleAsync(settings, ct)))
			.WithDescription("Attach a terminal to the existing server without starting another instance.");
		app.Map("health", async (IReplIoContext io, CancellationToken ct, [ReplOption] bool ready = false) =>
			Results.Exit(await ClientMode.ExecuteAsync(ready ? ["health", "--ready"] : ["health"], settings, io, ct)))
			.WithDescription("Probe the running process; --ready also requires a published index.");
		app.Map("exec", async (IReplIoContext io, CancellationToken ct, [ReplArgument] string[]? arguments = null) =>
			arguments is { Length: > 0 }
				? Results.Exit(await ClientMode.ExecuteAsync(arguments, settings, io, ct))
				: Results.Exit(2, "Usage: docs-index exec <tokens...>"))
			.WithDescription("Execute a command in the running server; all following tokens are forwarded unchanged.");
		app.Map("assets fetch", async (CancellationToken ct, [ReplArgument] string? rid = null) =>
			await Assets.FetchAsync(settings.Assets, rid ?? RuntimeInformation.RuntimeIdentifier, ct))
			.WithDescription("Download and verify the model, browser files and native extension for an optional runtime identifier.");
		return app;
	}
}
