using Microsoft.Extensions.DependencyInjection;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_HistoryProviders
{
	[TestMethod]
	[Description("Regression guard: verifies running interactive session so that history provider captures typed commands.")]
	public void When_RunningInteractiveSession_Then_HistoryProviderCapturesTypedCommands()
	{
		var spy = new SpyHistoryProvider();
		var sut = ReplApp.Create(services => services.AddSingleton<IHistoryProvider>(spy))
			.UseDefaultInteractive();
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.CaptureWithInput("hello\nexit\n", () => sut.Run([]));

		output.ExitCode.Should().Be(0);
		spy.Entries.Should().Equal(["hello", "exit"]);
	}

	[TestMethod]
	[Description("Regression guard: verifies custom history provider is registered so that default provider is not used.")]
	public async Task When_CustomHistoryProviderIsRegistered_Then_DefaultProviderIsNotUsed()
	{
		var spy = new SpyHistoryProvider();
		var sut = ReplApp.Create(services => services.AddSingleton<IHistoryProvider>(spy));
		sut.Map("history recent", async (IHistoryProvider history) => await history.GetRecentAsync(5).ConfigureAwait(false));

		var output = ConsoleCaptureHelper.CaptureWithInput("history recent\nexit\n", () => sut.Run([]));

		output.ExitCode.Should().Be(0);
		spy.Entries.Should().Contain("history recent");
		var recent = await spy.GetRecentAsync(maxCount: 10).ConfigureAwait(false);
		recent.Should().NotBeEmpty();
	}

	private sealed class SpyHistoryProvider : IHistoryProvider
	{
		private readonly List<string> _entries = [];

		public IReadOnlyList<string> Entries => _entries;

		public ValueTask AddAsync(string entry, CancellationToken cancellationToken = default)
		{
			_entries.Add(entry);
			return ValueTask.CompletedTask;
		}

		public ValueTask<IReadOnlyList<string>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default)
		{
			var skip = Math.Max(0, _entries.Count - maxCount);
			return ValueTask.FromResult<IReadOnlyList<string>>(_entries.Skip(skip).ToArray());
		}
	}
}



