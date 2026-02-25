namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_HostAbstraction
{
	[TestMethod]
	[Description("Regression guard: verifies custom host I/O is honored so that apps can run without using the process console streams directly.")]
	public void When_RunningWithCustomHost_Then_InputAndOutputAreRoutedThroughHost()
	{
		var input = new StringReader("ping" + Environment.NewLine);
		var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("ping", () => "pong");

		var exitCode = sut.Run(Array.Empty<string>(), host, new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None });

		exitCode.Should().Be(0);
		output.ToString().Should().Contain("pong");
	}

	[TestMethod]
	[Description("Regression guard: verifies async custom host run so that host-based executions can be awaited.")]
	public async Task When_RunningAsyncWithCustomHost_Then_InputAndOutputAreRoutedThroughHost()
	{
		var input = new StringReader("ping" + Environment.NewLine);
		var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("ping", () => "pong");

		var exitCode = await sut.RunAsync(
			Array.Empty<string>(),
			host,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None });

		exitCode.Should().Be(0);
		output.ToString().Should().Contain("pong");
	}

	private sealed class InMemoryHost(TextReader input, TextWriter output) : IReplHost
	{
		public TextReader Input { get; } = input;

		public TextWriter Output { get; } = output;
	}
}
