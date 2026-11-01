using System.Security.Cryptography;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Renderers.Html;

namespace DocsMcpSample.Search;

internal sealed class MarkdownCorpus(Settings settings, ITextEmbedding embedding)
{
	private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAutoIdentifiers().Build();

	internal IReadOnlyList<string> Files() => Directory.EnumerateFiles(settings.Documents, "*", new EnumerationOptions
	{
		RecurseSubdirectories = true,
		AttributesToSkip = FileAttributes.ReparsePoint,
		IgnoreInaccessible = false,
	}).Where(path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
		.Order(StringComparer.Ordinal).ToArray();

	internal string RelativePath(string path) => Path.GetRelativePath(settings.Documents, path).Replace('\\', '/');

	internal async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
	{
		if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { throw new IOException("Source links are not indexed."); }
		if (new FileInfo(path).Length > 8 * 1024 * 1024) { throw new IOException($"Markdown file exceeds the 8 MiB sample limit: {RelativePath(path)}"); }
		return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
	}

	internal async Task<string> FingerprintAsync(CancellationToken cancellationToken)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (var path in Files())
		{
			cancellationToken.ThrowIfCancellationRequested();
			hash.AppendData(Encoding.UTF8.GetBytes(RelativePath(path) + "\0"));
			hash.AppendData(SHA256.HashData(Encoding.UTF8.GetBytes(await ReadAsync(path, cancellationToken))));
		}
		return Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	internal SourceDocument Parse(string source, string markdown)
	{
		var documentId = "doc-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
		var blocks = Markdown.Parse(markdown, Pipeline);
		var title = Path.GetFileNameWithoutExtension(source);
		var heading = title;
		var anchor = "";
		var sections = new List<Section>();
		var pending = new StringBuilder();
		var startLine = 1;
		var endLine = 1;
		var part = 0;
		foreach (var block in blocks)
		{
			if (block is HeadingBlock headingBlock)
			{
				Flush();
				heading = Markdown.ToPlainText(markdown[block.Span.Start..(block.Span.End + 1)], Pipeline).Trim();
				if (sections.Count == 0 && pending.Length == 0 && block == blocks.FirstOrDefault()) { title = heading; }
				anchor = headingBlock.GetAttributes().Id!;
				part = 0;
				continue;
			}

			var text = markdown[block.Span.Start..(block.Span.End + 1)];
			var consumed = 0;
			while (consumed < text.Length)
			{
				if (pending.Length > 0 && embedding.CountPassageTokens(pending + "\n\n" + text[consumed..]) > E5Embedding.MaxTokens) { Flush(); }
				var remaining = text[consumed..];
				var length = FitPassagePrefix(remaining);
				var line = block.Line + 1 + text.AsSpan(0, consumed).Count('\n');
				if (pending.Length == 0) { startLine = line; }
				else { pending.Append("\n\n"); }
				pending.Append(remaining.AsSpan(0, length));
				endLine = line + remaining.AsSpan(0, length).Count('\n');
				consumed += length;
				if (consumed < text.Length) { Flush(); }
			}
		}
		Flush();
		return new(documentId, source, title, markdown, sections);

		void Flush()
		{
			if (pending.Length == 0) { return; }
			var sectionKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(anchor)))[..16];
			sections.Add(new($"{documentId}:{sectionKey}:{part++}", documentId, source, title, heading, anchor, startLine, endLine, pending.ToString()));
			pending.Clear();
		}
	}

	private int FitPassagePrefix(string text)
	{
		if (embedding.CountPassageTokens(text) <= E5Embedding.MaxTokens) { return text.Length; }
		var low = 0;
		var high = text.Length;
		while (low < high)
		{
			var middle = (low + high + 1) / 2;
			if (embedding.CountPassageTokens(text[..middle]) <= E5Embedding.MaxTokens) { low = middle; }
			else { high = middle - 1; }
		}
		if (low > 0 && char.IsHighSurrogate(text[low - 1])) { low--; }
		if (low == 0) { throw new InvalidOperationException("The tokenizer cannot fit one character in a passage."); }
		return low;
	}
}
