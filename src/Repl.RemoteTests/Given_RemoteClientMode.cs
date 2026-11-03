using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Repl.Remote;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_RemoteClientMode
{
	[TestMethod]
	[DataRow("--remote-console")]
	[DataRow("--remote-exec")]
	[Description("Client help is supplied by Repl without opening a pipe or constructing the web host.")]
	public async Task When_HelpIsRequested_Then_TheLauncherDescribesItsParameters(string mode)
	{
		var result = await RunAsync([mode, "--help"]).ConfigureAwait(false);

		result.ExitCode.Should().Be(0, "stdout: {0}; stderr: {1}", result.Output, result.Error);
		result.Output.Should().Contain("pipe");
		result.Error.Should().BeEmpty();
	}

	[TestMethod]
	[DataRow("--remote-console")]
	[DataRow("--remote-exec")]
	[Description("Repl rejects a missing pipe before a client tries to connect.")]
	public async Task When_ThePipeIsMissing_Then_ReplReportsAUsageError(string mode)
	{
		var result = await RunAsync([mode]).ConfigureAwait(false);

		result.ExitCode.Should().Be(2);
		result.Output.Should().Contain("pipe");
	}

	[TestMethod]
	[Description("The real executable forwards literal remote options and empty tokens while preserving output streams and the remote exit code.")]
	public async Task When_ExecRuns_Then_OnlyTheDestinationInterpretsItsTokens()
	{
		var pipe = TestPipeName.Create();
		var backend = new RecordingBackend();
		await using var server = new NamedPipeReplServer(pipe, _ => ValueTask.FromResult<IRemoteReplBackend>(backend));
		string[] tokens = ["echo", "", "a b", "a,b", "--help", "--output:unknown", "--", "x=y"];

		var result = await RunAsync(["--remote-exec", pipe, .. tokens]).ConfigureAwait(false);

		backend.Arguments.Should().Equal(tokens);
		result.ExitCode.Should().Be(17);
		result.Output.Should().Be("remote output\n{\"answer\":42}" + Environment.NewLine);
		result.Error.Should().Be("remote error\n");
	}

	[TestMethod]
	[Description("A destination alone is not an executable command and must not open a remote session.")]
	public async Task When_ExecHasNoCommand_Then_ItReportsAUsageError()
	{
		var result = await RunAsync(["--remote-exec", "unused-pipe"]).ConfigureAwait(false);

		result.ExitCode.Should().Be(2);
		result.Output.Should().Contain("command");
		result.Error.Should().BeEmpty();
	}

	[TestMethod]
	[Description("Unknown console options are rejected by Repl before opening a connection.")]
	public async Task When_ConsoleHasAnUnknownOption_Then_ItReportsAUsageError()
	{
		var result = await RunAsync(["--remote-console", "unused-pipe", "--unexpected"]).ConfigureAwait(false);

		result.ExitCode.Should().Be(2);
		result.Error.Should().BeEmpty();
	}

	private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] arguments)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var start = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false, CreateNoWindow = true,
			RedirectStandardOutput = true, RedirectStandardError = true,
		};
		start.ArgumentList.Add(HostingSample.AssemblyPath);
		foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the remote client sample.");
		var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
		var error = process.StandardError.ReadToEndAsync(deadline.Token);
		try
		{
			await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
			return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
		}
		finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
	}

	private sealed class RecordingBackend : IRemoteReplBackend
	{
		internal IReadOnlyList<string>? Arguments { get; private set; }
		public ValueTask<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken) =>
			ValueTask.FromResult(new RemoteCommandGraph("test", [], []));
		public ValueTask<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken)
		{
			Arguments = request.Arguments;
			return ValueTask.FromResult(new RemoteExecutionResult(17, "application",
				JsonSerializer.SerializeToElement(new { answer = 42 }), "remote output\n", "remote error\n"));
		}
		public ValueTask<IRemoteTerminalSession> OpenTerminalAsync(RemoteTerminalOptions options,
			Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken) => throw new NotSupportedException();
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
