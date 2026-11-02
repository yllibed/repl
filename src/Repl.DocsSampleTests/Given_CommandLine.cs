using System.Diagnostics;
using System.Text.Json;
using DocsMcpSample.Hosting;
using Repl.Remote;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_CommandLine
{
	[TestMethod]
	[DataRow("")]
	[DataRow("serve")]
	[DataRow("console")]
	[DataRow("health")]
	[DataRow("exec")]
	[DataRow("assets fetch")]
	[Description("Repl supplies launcher help without opening a pipe, downloading assets or constructing the indexing host.")]
	public async Task When_HelpIsRequested_Then_ItWorksWithoutAssetsOrAServer(string command)
	{
		using var fixture = new TestCorpus();
		using var output = new StringWriter();
		var app = CommandLine.Create(fixture.Settings);
		string[] arguments = ["--help", .. command.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

		var code = await app.RunAsync(arguments, new TextHost(output), new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.None }).ConfigureAwait(false);

		code.Should().Be(0);
		output.ToString().Should().Contain(command.Length == 0 ? "serve" : command);
		File.Exists(Path.Combine(fixture.Settings.Index, "documents.db")).Should().BeFalse();
	}

	[TestMethod]
	[Description("The startup hook selects serve for an empty invocation before the normal command invocation pipeline.")]
	public async Task When_NoCommandIsSupplied_Then_TheStartupHookSelectsServe()
	{
		using var fixture = new TestCorpus();
		var app = CommandLine.Create(fixture.Settings);
		string? selected = null;
		app.UseInvocationHook((ReplInvocationContext invocation) =>
		{
			selected = invocation.Parsed.Command;
			return ReplInvocationResult.Handled();
		});

		var code = await app.RunAsync([], new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.None }).ConfigureAwait(false);

		code.Should().Be(0);
		selected.Should().Be("serve");
	}

	[TestMethod]
	[DataRow("serve", "--unexpected")]
	[DataRow("console", "--unexpected")]
	[DataRow("health", "--unknown")]
	[DataRow("unknown", "command")]
	[Description("Invalid launcher input is refused by Repl before any listener, connection or asset action.")]
	public async Task When_LauncherInputIsInvalid_Then_ReplReturnsAUsageError(string first, string second)
	{
		using var fixture = new TestCorpus();
		using var output = new StringWriter();

		var code = await CommandLine.Create(fixture.Settings).RunAsync([first, second], new TextHost(output),
			new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.None }).ConfigureAwait(false);

		code.Should().Be(2);
		File.Exists(Path.Combine(fixture.Settings.Index, "documents.db")).Should().BeFalse();
	}

	[TestMethod]
	[DataRow(false, DisplayName = "JSON supplies the pipe setting")]
	[DataRow(true, DisplayName = "Environment overrides the JSON pipe setting")]
	[Description("Real launcher configuration selects the pipe from JSON or its environment override while remote tokens, streams and exit codes remain intact.")]
	public async Task When_ExecForwardsTokens_Then_OnlyTheRemoteProcessInterpretsThem(bool environmentOverridesFile)
	{
		using var fixture = new TestCorpus();
		var backend = new RecordingBackend();
		await using var listener = new NamedPipeReplServer(fixture.Settings.Pipe, _ => ValueTask.FromResult<IRemoteReplBackend>(backend));
		await File.WriteAllTextAsync(Path.Combine(fixture.Root, "appsettings.json"), JsonSerializer.Serialize(new
		{
			PIPE = environmentOverridesFile ? "unused-" + Guid.NewGuid().ToString("N") : fixture.Settings.Pipe,
		})).ConfigureAwait(false);
		string[] remote = ["docs", "search", "", "a b", "a,b", "--help", "--output:unknown", "--result:page-size=invalid", "--", "x=y"];
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var start = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
			WorkingDirectory = fixture.Root,
		};
		start.ArgumentList.Add(typeof(CommandLine).Assembly.Location);
		start.ArgumentList.Add("exec");
		foreach (var token in remote) { start.ArgumentList.Add(token); }
		start.Environment.Remove("DOCS_PIPE");
		if (environmentOverridesFile) { start.Environment["DOCS_PIPE"] = fixture.Settings.Pipe; }
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the sample launcher.");
		var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
		var error = process.StandardError.ReadToEndAsync(deadline.Token);

		try
		{
			await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

			process.ExitCode.Should().Be(17);
			backend.Arguments.Should().Equal(remote);
			(await output.ConfigureAwait(false)).Should().Be("remote output\n{\"answer\":42}" + Environment.NewLine);
			(await error.ConfigureAwait(false)).Should().Be("remote error\n");
		}
		finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
	}

	[TestMethod]
	[Description("A serve failure retains its process exit code and framework message while also printing the full exception to stderr.")]
	public async Task When_ServerStartupFails_Then_TheProcessReportsItsDiagnostic()
	{
		using var fixture = new TestCorpus();
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var start = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false, CreateNoWindow = true,
			RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fixture.Root,
		};
		start.ArgumentList.Add(typeof(CommandLine).Assembly.Location);
		start.ArgumentList.Add("serve");
		start.Environment["DOCS_DOCUMENTS"] = fixture.Settings.Documents;
		start.Environment["DOCS_INDEX"] = fixture.Settings.Index;
		start.Environment["DOCS_ASSETS"] = Path.Combine(fixture.Root, "missing-assets");
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the sample launcher.");
		var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
		var error = process.StandardError.ReadToEndAsync(deadline.Token);
		try
		{
			await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

			process.ExitCode.Should().Be(1);
			(await output.ConfigureAwait(false)).Should().Contain("Missing or modified asset");
			(await error.ConfigureAwait(false)).Should().Contain("Assets.VerifyAsync").And.Contain("Exception");
		}
		finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
	}

	private sealed class TextHost(TextWriter output) : IReplHost
	{
		public TextReader Input => TextReader.Null;
		public TextWriter Output => output;
	}

	private sealed class RecordingBackend : IRemoteReplBackend
	{
		internal IReadOnlyList<string>? Arguments { get; private set; }
		public ValueTask<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new RemoteCommandGraph("test", [], []));
		public ValueTask<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken)
		{
			Arguments = request.Arguments;
			return ValueTask.FromResult(new RemoteExecutionResult(17, "application", JsonSerializer.SerializeToElement(new { Answer = 42 },
				JsonSerializerOptions.Web), "remote output\n", "remote error\n"));
		}
		public ValueTask<IRemoteTerminalSession> OpenTerminalAsync(RemoteTerminalOptions options,
			Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken) => throw new NotSupportedException();
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
