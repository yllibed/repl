using System.Security.Cryptography;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;

namespace DocsMcpSample.Search;

internal sealed class MarkdownCorpus(Settings settings, ITextEmbedding embedding)
{
	private const int MaximumPassageCharacters = 8192;
	private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseYamlFrontMatter().UseAutoIdentifiers().Build();

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
			AppendFingerprint(hash, RelativePath(path), await ReadAsync(path, cancellationToken));
		}
		return Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	internal static void AppendFingerprint(IncrementalHash hash, string source, string text)
	{
		hash.AppendData(Encoding.UTF8.GetBytes(source + "\0"));
		hash.AppendData(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
	}

	// Keep context small even for generated headings. Clipping affects embeddings only, never source metadata.
	internal static string EmbeddingText(string title, string heading, string text)
	{
		return Clip(title) + "\n" + Clip(heading) + "\n\n" + text;

		static string Clip(string value)
		{
			var length = Math.Min(value.Length, 32);
			if (length < value.Length && length > 0 && char.IsHighSurrogate(value[length - 1])) { length--; }
			return value[..length];
		}
	}

	internal SourceDocument Parse(string source, string markdown, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var documentId = "doc-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
		var blocks = Markdown.Parse(markdown, Pipeline);
		cancellationToken.ThrowIfCancellationRequested();
		var title = blocks.OfType<HeadingBlock>().Select(HeadingText).FirstOrDefault(value => value.Length > 0)
			?? Path.GetFileNameWithoutExtension(source);
		var heading = title;
		var anchor = "";
		var sections = new List<Section>();
		var rangeStart = -1;
		var startLine = 1;
		var part = 0;
		foreach (var block in blocks)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (block is YamlFrontMatterBlock) { continue; }
			if (block is HeadingBlock headingBlock)
			{
				FlushRange(block.Span.Start);
				heading = HeadingText(headingBlock);
				anchor = headingBlock.GetAttributes().Id!;
				part = 0;
			}
			else if (rangeStart < 0)
			{
				rangeStart = block.Span.Start;
				startLine = block.Line + 1;
			}
		}
		FlushRange(markdown.Length);
		return new(documentId, source, title, markdown, sections);

		string HeadingText(HeadingBlock block) => Markdown.ToPlainText(markdown[block.Span.Start..(block.Span.End + 1)], Pipeline).Trim();

		void FlushRange(int end)
		{
			if (rangeStart < 0) { return; }
			var text = markdown[rangeStart..end];
			var consumed = 0;
			var line = startLine;
			var sectionKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(anchor)))[..16];
			while (consumed < text.Length)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var length = FitPassagePrefix(title, heading, text, consumed, cancellationToken);
				var passage = text.Substring(consumed, length);
				var newlines = passage.AsSpan().Count('\n');
				var endLine = line + newlines - (passage.EndsWith('\n') ? 1 : 0);
				sections.Add(new($"{documentId}:{sectionKey}:{part++}", documentId, source, title, heading, anchor, line, endLine, passage));
				line += newlines;
				consumed += length;
			}
			rangeStart = -1;
		}
	}

	private int FitPassagePrefix(string title, string heading, string text, int offset, CancellationToken cancellationToken)
	{
		var low = 0;
		var high = Math.Min(text.Length - offset, MaximumPassageCharacters - EmbeddingText(title, heading, "").Length);
		high = SafeLength(high);
		if (Count(high) <= embedding.MaxTokens) { low = high; }
		else
		{
			while (low < high)
			{
				var middle = (low + high + 1) / 2;
				if (Count(middle) <= embedding.MaxTokens) { low = middle; }
				else { high = middle - 1; }
			}
		}
		low = SafeLength(low);
		if (low == 0) { throw new InvalidOperationException("The tokenizer cannot fit context and one character in a passage."); }
		if (offset + low < text.Length)
		{
			var newline = text.AsSpan(offset, low).LastIndexOf('\n');
			if (newline >= 0) { low = newline + 1; }
		}
		// A shorter prefix can tokenize differently at its new boundary. Check the final, exact input too.
		while (Count(low) > embedding.MaxTokens)
		{
			low = SafeLength(low - 1);
			if (low == 0) { throw new InvalidOperationException("The tokenizer cannot fit context and one character in a passage."); }
		}
		return low;

		int Count(int length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return embedding.CountPassageTokens(EmbeddingText(title, heading, text.Substring(offset, SafeLength(length))));
		}

		int SafeLength(int length)
		{
			if (length > 0 && offset + length < text.Length
				&& (char.IsHighSurrogate(text[offset + length - 1])
					|| (text[offset + length - 1] == '\r' && text[offset + length] == '\n'))) { length--; }
			return length;
		}
	}
}
