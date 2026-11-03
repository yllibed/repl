using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DocsMcpSample;

internal static class Assets
{
	private sealed record Asset(string Path, string Url, string Sha256, string? Extract, string? Rid);
	private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNameCaseInsensitive = true };
	private static Asset[] ForPlatform(string rid)
	{
		var manifest = JsonSerializer.Deserialize<Asset[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets.json")), ManifestOptions)!;
		if (!manifest.Any(asset => asset.Rid == rid)) { throw new PlatformNotSupportedException($"No pinned native assets for {rid}. Supported targets: win-x64, linux-x64, osx-arm64. The selected ONNX Runtime package does not include macOS Intel binaries."); }
		return manifest.Where(asset => asset.Rid is null || asset.Rid == rid).ToArray();
	}

	internal static async Task FetchAsync(string directory, string rid, CancellationToken cancellationToken)
	{
		using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		foreach (var asset in ForPlatform(rid))
		{
			var path = Path.Combine(directory, asset.Path);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			if (!await MatchesAsync(path, asset.Sha256, cancellationToken))
			{
				Console.WriteLine($"Downloading {asset.Path}...");
				await DownloadAsync(http, asset.Url, path, asset.Sha256, TimeSpan.FromMinutes(10), cancellationToken);
			}
			if (asset.Extract is not null)
			{
				var bytes = ReadExtension(path, asset.Extract);
				await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, asset.Extract), bytes, cancellationToken);
			}
		}
		Console.WriteLine($"Verified assets for {rid} in {directory}");
	}

	internal static async Task DownloadAsync(HttpClient http, string url, string path, string sha256, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(timeout);
		var temporary = path + ".download";
		try
		{
			using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
			response.EnsureSuccessStatusCode();
			await using (var file = File.Create(temporary)) { await response.Content.CopyToAsync(file, deadline.Token); }
			if (!await MatchesAsync(temporary, sha256, deadline.Token)) { throw new IOException($"Checksum mismatch for {Path.GetFileName(path)}."); }
			deadline.Token.ThrowIfCancellationRequested();
			File.Move(temporary, path, overwrite: true);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException($"Download timed out for {Path.GetFileName(path)}. Run assets fetch to retry.");
		}
		finally { File.Delete(temporary); }
	}

	internal static async Task VerifyAsync(string directory, CancellationToken cancellationToken)
	{
		foreach (var asset in ForPlatform(RuntimeInformation.RuntimeIdentifier))
		{
			var path = Path.Combine(directory, asset.Path);
			if (!await MatchesAsync(path, asset.Sha256, cancellationToken)) { throw new IOException($"Missing or modified asset: {asset.Path}. Run assets fetch."); }
			if (asset.Extract is not null)
			{
				var target = Path.Combine(Path.GetDirectoryName(path)!, asset.Extract);
				var expected = Convert.ToHexStringLower(SHA256.HashData(ReadExtension(path, asset.Extract)));
				if (!await MatchesAsync(target, expected, cancellationToken)) { throw new IOException($"Missing or modified extension: {target}. Run assets fetch."); }
			}
		}
	}

	private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken cancellationToken)
	{
		if (!File.Exists(path)) { return false; }
		await using var stream = File.OpenRead(path);
		return string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)), expected, StringComparison.Ordinal);
	}

	private static byte[] ReadExtension(string archive, string name)
	{
		using var file = File.OpenRead(archive);
		using var gzip = new GZipStream(file, CompressionMode.Decompress);
		using var tar = new TarReader(gzip);
		while (tar.GetNextEntry() is { } entry)
		{
			if (Path.GetFileName(entry.Name) != name || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) { continue; }
			using var buffer = new MemoryStream();
			entry.DataStream!.CopyTo(buffer);
			return buffer.ToArray();
		}
		throw new IOException($"Native archive does not contain {name}.");
	}
}
