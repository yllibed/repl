using System.Net;
using System.Security.Cryptography;
using DocsMcpSample;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_AssetDownloads
{
	[TestMethod]
	[DataRow(false, DisplayName = "Download deadline expires after headers")]
	[DataRow(true, DisplayName = "Caller cancels after headers")]
	[Description("A stalled response body is interrupted and its partial file removed without replacing an existing asset; caller cancellation keeps its original meaning.")]
	public async Task When_TheBodyStalls_Then_TheDownloadEndsWithoutPublishing(bool cancelCaller)
	{
		using var fixture = new TestCorpus();
		var path = Path.Combine(fixture.Settings.Index, "asset.bin");
		await File.WriteAllTextAsync(path, "original").ConfigureAwait(false);
		using var caller = new CancellationTokenSource();
		using var content = new StalledContent();
		using var http = new HttpClient(new ResponseHandler(content)) { Timeout = Timeout.InfiniteTimeSpan };
		var download = Assets.DownloadAsync(http, "https://assets.example/test", path, "unused", cancelCaller ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(1), caller.Token);
		await content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		if (cancelCaller) { await caller.CancelAsync().ConfigureAwait(false); }
		var finish = () => download.WaitAsync(TimeSpan.FromSeconds(5));
		if (cancelCaller) { await finish.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false); }
		else { await finish.Should().ThrowAsync<TimeoutException>().WithMessage("Download timed out*").ConfigureAwait(false); }

		File.Exists(path + ".download").Should().BeFalse();
		(await File.ReadAllTextAsync(path).ConfigureAwait(false)).Should().Be("original");
	}

	[TestMethod]
	[Description("A complete response is hash-verified before it replaces the destination and leaves no partial download behind.")]
	public async Task When_TheBodyCompletes_Then_OnlyVerifiedContentIsPublished()
	{
		using var fixture = new TestCorpus();
		var path = Path.Combine(fixture.Settings.Index, "asset.bin");
		var bytes = "verified asset"u8.ToArray();
		using var http = new HttpClient(new ResponseHandler(new ByteArrayContent(bytes)));

		await Assets.DownloadAsync(http, "https://assets.example/test", path, Convert.ToHexStringLower(SHA256.HashData(bytes)), TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);

		(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).Should().Equal(bytes);
		File.Exists(path + ".download").Should().BeFalse();
	}

	private sealed class ResponseHandler(HttpContent content) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
	}

	private sealed class StalledContent : HttpContent
	{
		internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
		{
			await stream.WriteAsync("partial"u8.ToArray(), cancellationToken).ConfigureAwait(false);
			Entered.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
		}
		protected override bool TryComputeLength(out long length) { length = 0; return false; }
	}
}
