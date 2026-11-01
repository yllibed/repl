using System.Diagnostics;
using AwesomeAssertions;
using Repl.Remote;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_RemoteInterop
{
	[TestMethod]
	[TestCategory("ExternalToolchain")]
	[Description("A Python standard-library client proves that framing, JSON values, discovery and errors do not depend on the .NET client or CLR serialization.")]
	public async Task When_AStandardJsonRpcClientConnects_Then_TheWireContractIsPortable()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "protocol_client.py"));
		using var process = Process.Start(start)!;
		var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
		var backend = new Backend();
		await using var backendOwner = backend.ConfigureAwait(false);
		try
		{
			await RemoteReplServer.RunAsync(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, backend, deadline.Token).ConfigureAwait(false);
			await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
			process.ExitCode.Should().Be(0, await error.ConfigureAwait(false));
			backend.Executions.Should().Be(1);
		}
		finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
	}

	private sealed class Backend : IRemoteReplBackend
	{
		public int Executions { get; private set; }
		public ValueTask<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new RemoteCommandGraph("python-interop", [], []));
		public ValueTask<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken)
		{
			Executions++;
			return ValueTask.FromResult(new RemoteExecutionResult(17, "success", request.Values!["value"]));
		}
		public ValueTask<IRemoteTerminalSession> OpenTerminalAsync(RemoteTerminalOptions options, Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken) => throw new NotSupportedException();
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
