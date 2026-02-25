using Microsoft.Extensions.DependencyInjection;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_HistoryAmbientCommand
{
	[TestMethod]
	[Description("Regression guard: verifies interactive history command is used so that recent commands are rendered.")]
	public void When_InteractiveHistoryCommandIsUsed_Then_RecentCommandsAreRendered()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"hello\nhistory\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("hello");
		output.Text.Should().Contain("history");
	}

	[TestMethod]
	[Description("Regression guard: verifies interactive history command uses limit so that result count is bounded.")]
	public void When_InteractiveHistoryCommandUsesLimit_Then_ResultCountIsBounded()
	{
		var spy = new SpyHistoryProvider();
		var sut = ReplApp.Create(services => services.AddSingleton<IHistoryProvider>(spy))
			.UseDefaultInteractive();

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"history --limit 1\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		spy.LastRequestedMaxCount.Should().Be(1);
		output.Text.Should().NotContain("Unknown command");
	}

	private sealed class SpyHistoryProvider : IHistoryProvider
	{
		private readonly List<string> _entries = ["seed-entry"];

		public int LastRequestedMaxCount { get; private set; }

		public ValueTask AddAsync(string entry, CancellationToken cancellationToken = default)
		{
			_entries.Add(entry);
			return ValueTask.CompletedTask;
		}

		public ValueTask<IReadOnlyList<string>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default)
		{
			LastRequestedMaxCount = maxCount;
			return ValueTask.FromResult<IReadOnlyList<string>>(_entries.TakeLast(maxCount).ToArray());
		}
	}
}


