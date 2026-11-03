using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DocsMcpSample.Search;

internal sealed partial class DocumentIndex(Settings settings, ITextEmbedding embedding, TimeProvider time)
{
	private const int SchemaVersion = 1;
	private const int RrfRankOffset = 60;
	private string DatabasePath => Path.Combine(settings.Index, "documents.db");
	internal string ExtensionPath => Path.Combine(settings.Assets, "native", RuntimeInformation.RuntimeIdentifier,
		OperatingSystem.IsWindows() ? "vec0.dll" : OperatingSystem.IsMacOS() ? "vec0.dylib" : "vec0.so");

	internal SqliteConnection Open(string? path = null)
	{
		var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? DatabasePath, Pooling = false }.ToString());
		try
		{
			connection.Open();
			connection.LoadExtension(ExtensionPath);
			connection.EnableExtensions(false);
			return connection;
		}
		catch { connection.Dispose(); throw; }
	}

	internal void Initialize()
	{
		using var connection = Open();
		Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
		CreateTables(connection);
	}

	private void CreateTables(SqliteConnection connection) => Execute(connection, $$"""
		CREATE TABLE IF NOT EXISTS metadata(id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, generation TEXT NOT NULL);
		CREATE TABLE IF NOT EXISTS documents(id TEXT PRIMARY KEY, source TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL);
		CREATE TABLE IF NOT EXISTS sections(rowid INTEGER PRIMARY KEY, id TEXT UNIQUE NOT NULL, document_id TEXT NOT NULL,
		 source TEXT NOT NULL, title TEXT NOT NULL, heading TEXT NOT NULL, anchor TEXT NOT NULL, start_line INTEGER NOT NULL, end_line INTEGER NOT NULL, body TEXT NOT NULL);
		CREATE VIRTUAL TABLE IF NOT EXISTS keywords USING fts5(title, heading, body, tokenize='unicode61 remove_diacritics 2');
		CREATE VIRTUAL TABLE IF NOT EXISTS vectors USING vec0(id INTEGER PRIMARY KEY, embedding float[{{embedding.Dimensions}}] distance_metric=cosine);
		""");

	internal IndexGeneration? Generation(SqliteConnection? connection = null)
	{
		using var owner = connection is null ? Open() : null;
		using var command = (connection ?? owner!).CreateCommand();
		command.CommandText = "SELECT version,generation FROM metadata WHERE id=1";
		using var reader = command.ExecuteReader();
		if (!reader.Read() || reader.GetInt32(0) != SchemaVersion) { return null; }
		var generation = JsonSerializer.Deserialize<IndexGeneration>(reader.GetString(1));
		return generation?.Model == embedding.Identity ? generation : null;
	}

	internal async Task<IndexGeneration> RebuildAsync(MarkdownCorpus corpus, Action<int, int> progress, CancellationToken cancellationToken)
	{
		// Stage on disk: embeddings are prepared without holding the live database's writer lock.
		var stagingPath = Path.Combine(settings.Index, $"build-{Guid.NewGuid():N}.db");
		try
		{
			using var stagedContent = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			using var staging = Open(stagingPath);
			CreateTables(staging);
			var documents = 0;
			var sections = 0;
			using (var transaction = staging.BeginTransaction())
			{
				foreach (var path in corpus.Files())
				{
					cancellationToken.ThrowIfCancellationRequested();
					var document = corpus.Parse(corpus.RelativePath(path), await corpus.ReadAsync(path, cancellationToken), cancellationToken);
					MarkdownCorpus.AppendFingerprint(stagedContent, document.Source, document.Text);
					Execute(staging, "INSERT INTO documents VALUES($1,$2,$3,$4)", document.Id, document.Source, document.Title, document.Text);
					foreach (var section in document.Sections)
					{
						var vector = embedding.Embed(section.Text, query: false, cancellationToken);
						cancellationToken.ThrowIfCancellationRequested();
						var rowid = ++sections;
						Execute(staging, "INSERT INTO sections VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
							rowid, section.Id, section.DocumentId, section.Source, section.Title, section.Heading, section.Anchor, section.StartLine, section.EndLine, section.Text);
						Execute(staging, "INSERT INTO keywords(rowid,title,heading,body) VALUES($1,$2,$3,$4)", rowid, section.Title, section.Heading, section.Text);
						Execute(staging, "INSERT INTO vectors(id,embedding) VALUES($1,$2)", rowid, MemoryMarshal.AsBytes(vector.AsSpan()).ToArray());
						progress(documents, sections);
					}
					progress(++documents, sections);
				}
				transaction.Commit();
			}
			var fingerprint = Convert.ToHexStringLower(stagedContent.GetHashAndReset());
			if (fingerprint != await corpus.FingerprintAsync(cancellationToken)) { throw new IOException("Documents changed during indexing; the next scan will retry."); }
			var generation = new IndexGeneration(Guid.NewGuid().ToString("N")[..12], fingerprint, embedding.Identity, documents, sections, time.GetUtcNow());
			using var live = Open();
			Execute(live, "ATTACH DATABASE $1 AS replacement", stagingPath);
			using var publish = live.BeginTransaction();
			Execute(live, "DELETE FROM documents; DELETE FROM sections; DELETE FROM keywords; DELETE FROM vectors; DELETE FROM metadata;");
			Execute(live, """
				INSERT INTO documents(id,source,title,body) SELECT id,source,title,body FROM replacement.documents;
				INSERT INTO sections(rowid,id,document_id,source,title,heading,anchor,start_line,end_line,body)
				 SELECT rowid,id,document_id,source,title,heading,anchor,start_line,end_line,body FROM replacement.sections;
				INSERT INTO keywords(rowid,title,heading,body) SELECT rowid,title,heading,body FROM replacement.keywords;
				INSERT INTO vectors(id,embedding) SELECT id,embedding FROM replacement.vectors;
				""");
			Execute(live, "INSERT INTO metadata VALUES(1,$1,$2)", SchemaVersion, JsonSerializer.Serialize(generation));
			cancellationToken.ThrowIfCancellationRequested();
			publish.Commit();
			return generation;
		}
		finally { File.Delete(stagingPath); }
	}

	internal SearchComparison Search(string query, int count, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) { throw new ArgumentException("Provide a search query between 1 and 2,000 characters."); }
		if (count is < 1 or > 20) { throw new ArgumentOutOfRangeException(nameof(count), "Choose 1 to 20 results."); }
		var vector = embedding.Embed(query, query: true, cancellationToken);
		using var connection = Open();
		using var transaction = connection.BeginTransaction(deferred: true);
		var generation = RequireGeneration(connection);
		var candidateCount = Math.Max(20, count * 4);
		var terms = SearchTerms().Matches(query).Select(match => "\"" + match.Value + "\"");
		var expression = string.Join(" OR ", terms);
		var keyword = expression.Length == 0 ? [] : ReadHits(connection, """
			SELECT s.rowid,s.id,s.document_id,s.source,s.title,s.heading,s.anchor,s.start_line,s.end_line,s.body,
			 bm25(keywords,3,2,1) AS score FROM keywords JOIN sections s ON s.rowid=keywords.rowid
			WHERE keywords MATCH $query ORDER BY score,s.id LIMIT $count
			""", expression, candidateCount);
		var semantic = ReadHits(connection, """
			SELECT s.rowid,s.id,s.document_id,s.source,s.title,s.heading,s.anchor,s.start_line,s.end_line,s.body,
			 v.distance AS score FROM (SELECT id,distance FROM vectors WHERE embedding MATCH $query AND k=$count) v
			JOIN sections s ON s.rowid=v.id ORDER BY score,s.id
			""", MemoryMarshal.AsBytes(vector.AsSpan()).ToArray(), candidateCount);
		cancellationToken.ThrowIfCancellationRequested();
		return new(query, generation.Id, keyword.Take(count).ToArray(), semantic.Take(count).ToArray(), Fuse(keyword, semantic, count));
	}

	internal static IReadOnlyList<SearchHit> Fuse(IReadOnlyList<SearchHit> keyword, IReadOnlyList<SearchHit> vector, int count) =>
		keyword.Select((hit, rank) => hit with { Score = 1d / (RrfRankOffset + rank + 1) })
			.Concat(vector.Select((hit, rank) => hit with { Score = 1d / (RrfRankOffset + rank + 1) }))
			.GroupBy(hit => hit.Id, StringComparer.Ordinal)
			.Select(group => group.First() with { Score = group.Sum(hit => hit.Score) })
			.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Id, StringComparer.Ordinal).Take(count).ToArray();

	internal FetchedDocument Fetch(string id, int maxChars)
	{
		if (string.IsNullOrWhiteSpace(id) || id.Length > 300) { throw new ArgumentException("Use an identifier returned by search or docs list."); }
		if (maxChars is < 100 or > 20000) { throw new ArgumentOutOfRangeException(nameof(maxChars), "Choose 100 to 20,000 characters."); }
		using var connection = Open();
		using var transaction = connection.BeginTransaction(deferred: true);
		var generation = RequireGeneration(connection);
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT source,title,body FROM documents WHERE id=$id UNION ALL SELECT source,title,body FROM sections WHERE id=$id LIMIT 1";
		command.Parameters.AddWithValue("$id", id);
		using var reader = command.ExecuteReader();
		if (!reader.Read()) { throw new KeyNotFoundException("Identifier not found in the current index. Search again for an updated reference."); }
		var text = reader.GetString(2);
		var length = Math.Min(text.Length, maxChars);
		if (length < text.Length && char.IsHighSurrogate(text[length - 1])) { length--; }
		return new(id, reader.GetString(0), reader.GetString(1), text[..length], text.Length > length, generation.Id);
	}

	internal DocumentSummary[] List(int offset, int count, CancellationToken cancellationToken = default)
	{
		// Repl probes one row beyond its maximum 1,000-row page to detect a continuation.
		if (offset < 0 || count is < 1 or > 1001) { throw new ArgumentException("Use a nonnegative offset and a count between 1 and 1,001."); }
		cancellationToken.ThrowIfCancellationRequested();
		using var connection = Open();
		using var transaction = connection.BeginTransaction(deferred: true);
		RequireGeneration(connection);
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT id,source,title FROM documents ORDER BY source LIMIT $count OFFSET $offset";
		command.Parameters.AddWithValue("$count", count);
		command.Parameters.AddWithValue("$offset", offset);
		using var reader = command.ExecuteReader();
		var result = new List<DocumentSummary>();
		while (reader.Read())
		{
			cancellationToken.ThrowIfCancellationRequested();
			result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
		}
		return result.ToArray();
	}

	private IndexGeneration RequireGeneration(SqliteConnection connection) => Generation(connection) ?? throw new IndexUnavailableException("The documentation index is not ready. Check index status for progress or failure details.");

	private static List<SearchHit> ReadHits(SqliteConnection connection, string sql, object query, int count)
	{
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.Parameters.AddWithValue("$query", query);
		command.Parameters.AddWithValue("$count", count);
		using var reader = command.ExecuteReader();
		var result = new List<SearchHit>();
		while (reader.Read())
		{
			var text = reader.GetString(9);
			var excerptLength = Math.Min(text.Length, 500);
			if (excerptLength < text.Length && char.IsHighSurrogate(text[excerptLength - 1])) { excerptLength--; }
			result.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
				reader.GetInt32(7), reader.GetInt32(8), text[..excerptLength], reader.GetDouble(10)));
		}
		return result;
	}

	internal static void Execute(SqliteConnection connection, string sql, params object[] values)
	{
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		for (var i = 0; i < values.Length; i++) { command.Parameters.AddWithValue("$" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), values[i]); }
		command.ExecuteNonQuery();
	}

	[GeneratedRegex(@"[\p{L}\p{Nd}_]+")]
	private static partial Regex SearchTerms();
}
