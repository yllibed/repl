using System.Net;
using DocsMcpSample;
using DocsMcpSample.Search;
using Microsoft.Extensions.Time.Testing;

namespace Repl.DocsSampleTests;

internal sealed class TestCorpus : IDisposable
{
	internal string Root { get; } = Path.Combine(Path.GetTempPath(), "repl-docs-tests", Guid.NewGuid().ToString("N"));
	internal Settings Settings { get; }
	internal TestEmbedding Embedding { get; } = new();
	internal FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
	internal MarkdownCorpus Corpus { get; }
	internal DocumentIndex Index { get; }

	internal TestCorpus()
	{
		Settings = new(Path.Combine(Root, "documents"), Path.Combine(Root, "index"), Environment.GetEnvironmentVariable("DOCS_TEST_ASSETS") ?? Root,
			"docs-test-" + Guid.NewGuid().ToString("N"), IPAddress.Loopback, 8080, 8081);
		Directory.CreateDirectory(Settings.Documents);
		Directory.CreateDirectory(Settings.Index);
		Corpus = new(Settings, Embedding);
		Index = new(Settings, Embedding, Clock);
	}

	internal Task WriteAsync(string name, string markdown) => File.WriteAllTextAsync(Path.Combine(Settings.Documents, name), markdown);
	public void Dispose() => Directory.Delete(Root, recursive: true);
}
