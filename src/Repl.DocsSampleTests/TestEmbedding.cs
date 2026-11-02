using DocsMcpSample.Search;

namespace Repl.DocsSampleTests;

internal sealed class TestEmbedding : ITextEmbedding
{
	public string Identity => "test-embedding-v1";
	public int Dimensions => 4;
	internal Action<int>? Counting { get; set; }
	public int CountPassageTokens(string text) { Counting?.Invoke(text.Length); return text.Length + 2; }
	internal Action? BeforeEmbedding { get; set; }
	internal Action<CancellationToken>? Preparing { get; set; }
	public float[] Embed(string text, bool query, CancellationToken cancellationToken)
	{
		BeforeEmbedding?.Invoke();
		Preparing?.Invoke(cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		return text.Contains("console", StringComparison.OrdinalIgnoreCase) ? [1, 0, 0, 0] : [0, 1, 0, 0];
	}
}
