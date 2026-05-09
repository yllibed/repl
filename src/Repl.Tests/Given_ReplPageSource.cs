namespace Repl.Tests;

using System.Runtime.CompilerServices;

[TestClass]
public sealed class Given_ReplPageSource
{
	[TestMethod]
	[Description("ReplPageSource.FromItems uses offset cursors so in-memory result sets can be paged without custom interface implementations.")]
	public async Task When_FromItemsFetchesPages_Then_EmitsOffsetCursor()
	{
		var source = ReplPageSource.FromItems(["one", "two", "three"]);

		var first = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));
		var second = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: first.PageInfo.NextCursor,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		first.Items.Should().Equal("one", "two");
		first.PageInfo.NextCursor.Should().Be("2");
		first.PageInfo.HasMore.Should().BeTrue();
		second.Items.Should().Equal("three");
		second.PageInfo.NextCursor.Should().BeNull();
		second.PageInfo.HasMore.Should().BeFalse();
	}

	[TestMethod]
	[Description("ReplPageSource.FromOffset fetches one extra row so offset-based stores do not need to compute totals.")]
	public async Task When_FromOffsetFetchesPages_Then_EmitsOffsetCursor()
	{
		var all = new[] { "one", "two", "three" };
		var source = ReplPageSource.FromOffset<string>((offset, take, _) =>
			ValueTask.FromResult<IReadOnlyList<string>>(all.Skip(offset).Take(take).ToArray()), all.Length);

		var first = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));
		var second = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: first.PageInfo.NextCursor,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		first.Items.Should().Equal("one", "two");
		first.PageInfo.NextCursor.Should().Be("2");
		first.PageInfo.TotalCount.Should().Be(3);
		second.Items.Should().Equal("three");
		second.PageInfo.HasMore.Should().BeFalse();
	}

	[TestMethod]
	[Description("ReplPageSource.FromOffset supports state arguments so handlers can use static lambdas without closure allocations.")]
	public async Task When_FromOffsetUsesState_Then_StaticFetchCanReadState()
	{
		var state = new PageStore(["one", "two", "three"]);
		var source = ReplPageSource.FromOffset<string, PageStore>(
			state,
			static (store, offset, take, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>(store.Items.Skip(offset).Take(take).ToArray()));

		var page = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		page.Items.Should().Equal("one", "two");
		page.PageInfo.NextCursor.Should().Be("2");
		page.PageInfo.TotalCount.Should().BeNull();
	}

	[TestMethod]
	[Description("ReplPageSource.FromOffset can apply a client-side filter after source paging and before the final page is emitted.")]
	public async Task When_FromOffsetUsesClientSideFilter_Then_PageContainsFilteredItems()
	{
		var state = new PageStore(["one", "two", "three", "four", "five", "six"]);
		var source = ReplPageSource.FromOffset<string, PageStore>(
			state,
			static (store, offset, take, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>(store.Items.Skip(offset).Take(take).ToArray()),
			filter: static (_, item) => item.Length == 3);

		var first = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));
		var second = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: first.PageInfo.NextCursor,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		first.Items.Should().Equal("one", "two");
		first.PageInfo.NextCursor.Should().Be("2");
		first.PageInfo.TotalCount.Should().BeNull();
		second.Items.Should().Equal("six");
		second.PageInfo.HasMore.Should().BeFalse();
	}

	[TestMethod]
	[Description("ReplPageSource.FromOffset fails clearly when All is requested because unbounded source paging can exhaust memory.")]
	public async Task When_FromOffsetReceivesAllRequest_Then_FailsClearly()
	{
		var source = ReplPageSource.FromOffset<int>(
			static (_, take, _) => ValueTask.FromResult<IReadOnlyList<int>>(Enumerable.Range(0, take).ToArray()));

		var action = async () => await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: true,
				Surface: ReplResultSurface.Console)).ConfigureAwait(false);

		await action.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*--result:all*not supported*")
			.ConfigureAwait(false);
	}

	[TestMethod]
	[Description("ReplPageSource.FromOffset treats an explicit zero cursor as the first offset.")]
	public async Task When_FromOffsetReceivesZeroCursor_Then_StartsAtFirstItem()
	{
		var source = ReplPageSource.FromOffset<int>(
			static (offset, take, _) => ValueTask.FromResult<IReadOnlyList<int>>(
				Enumerable.Range(offset, take).ToArray()));

		var page = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: "0",
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		page.Items.Should().Equal(0, 1);
	}

	[TestMethod]
	[Description("ReplPageSource.FromItems rejects malformed offset cursors instead of silently replaying the first page.")]
	public async Task When_FromItemsReceivesMalformedCursor_Then_FailsClearly()
	{
		var source = ReplPageSource.FromItems(["one", "two", "three"]);

		var action = async () => await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: "abc",
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console)).ConfigureAwait(false);

		await action.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*cursor*offset*")
			.ConfigureAwait(false);
	}

	[TestMethod]
	[Description("ReplPageSource.FromOffset rejects negative offset cursors instead of silently replaying the first page.")]
	public async Task When_FromOffsetReceivesNegativeCursor_Then_FailsClearly()
	{
		var source = ReplPageSource.FromOffset<int>(
			static (offset, take, _) => ValueTask.FromResult<IReadOnlyList<int>>(
				Enumerable.Range(offset, take).ToArray()));

		var action = async () => await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: "-1",
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console)).ConfigureAwait(false);

		await action.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*cursor*offset*")
			.ConfigureAwait(false);
	}

	[TestMethod]
	[Description("ReplPageSource.FromAsyncEnumerable pages async streams with offset cursors.")]
	public async Task When_FromAsyncEnumerableFetchesPages_Then_EmitsOffsetCursor()
	{
		var source = ReplPageSource.FromAsyncEnumerable(_ => ReadItemsAsync(["one", "two", "three"]));

		var first = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));
		var second = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: first.PageInfo.NextCursor,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		first.Items.Should().Equal("one", "two");
		first.PageInfo.NextCursor.Should().Be("2");
		first.PageInfo.HasMore.Should().BeTrue();
		second.Items.Should().Equal("three");
		second.PageInfo.NextCursor.Should().BeNull();
		second.PageInfo.HasMore.Should().BeFalse();
	}

	[TestMethod]
	[Description("ReplPageSource.FromAsyncEnumerable requires deterministic replay so page two returns the raw offset continuation.")]
	public async Task When_FromAsyncEnumerableFactoryIsDeterministic_Then_SecondPageUsesRawOffset()
	{
		var source = ReplPageSource.FromAsyncEnumerable(_ => ReadItemsAsync(["one", "two", "three", "four"]));

		var first = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));
		var second = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: first.PageInfo.NextCursor,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		second.Items.Should().Equal("three", "four");
		second.PageInfo.Cursor.Should().Be("2");
	}

	[TestMethod]
	[Description("ReplPageSource.FromAsyncEnumerable requires a replayable factory and fails clearly when the factory returns a single-use stream.")]
	public async Task When_FromAsyncEnumerableFactoryIsNotReplayable_Then_SecondPageFailsClearly()
	{
		var state = new SingleUseAsyncEnumerable<string>(["one", "two", "three"]);
		var source = ReplPageSource.FromAsyncEnumerable(_ => state);

		var first = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		var action = async () => await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: first.PageInfo.NextCursor,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console)).ConfigureAwait(false);

		await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
	}

	[TestMethod]
	[Description("ReplPageSource.FromAsyncEnumerable starts scan-limit accounting after the raw offset skip.")]
	public async Task When_FromAsyncEnumerableUsesDeepOffset_Then_ScanLimitAppliesAfterOffset()
	{
		var source = ReplPageSource.FromAsyncEnumerable(
			_ => ReadIntItemsAsync(Enumerable.Range(0, 100)),
			filter: static _ => true,
			maxSourceItemsToScan: 3);

		var page = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: "50",
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		page.Items.Should().Equal(50, 51);
		page.PageInfo.NextCursor.Should().Be("52");
	}

	[TestMethod]
	[Description("ReplPageSource.FromOffset enforces the client-side filter scan limit per source item.")]
	public async Task When_FilteredOffsetSourceExceedsScanLimit_Then_FailsBeforeFetchingAnotherBatch()
	{
		var fetches = 0;
		var source = ReplPageSource.FromOffset<int>(
			(_, take, _) =>
			{
				fetches++;
				return ValueTask.FromResult<IReadOnlyList<int>>(Enumerable.Range(0, take).ToArray());
			},
			filter: static _ => false,
			maxSourceItemsToScan: 2);

		var action = async () => await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console)).ConfigureAwait(false);

		await action.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*scan limit*")
			.ConfigureAwait(false);
		fetches.Should().Be(1);
	}

	[TestMethod]
	[Description("ReplPageSource.FromAsyncEnumerable passes cancellation to the async stream.")]
	public async Task When_FromAsyncEnumerableIsCancelled_Then_SourceObservesCancellation()
	{
		using var cts = new CancellationTokenSource();
		var observed = false;
		var source = ReplPageSource.FromAsyncEnumerable(ct => ReadUntilCancelledAsync(() => observed = true, ct));
		await cts.CancelAsync().ConfigureAwait(false);

		var action = async () => await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console),
			cts.Token).ConfigureAwait(false);

		await action.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		observed.Should().BeTrue();
	}

	[TestMethod]
	[Description("ReplPageSource.FromAsyncEnumerable supports state arguments and client-side filtering over replayable streams.")]
	public async Task When_FromAsyncEnumerableUsesStateAndFilter_Then_StaticFactoryCanReadState()
	{
		var state = new PageStore(["one", "two", "three", "four"]);
		var source = ReplPageSource.FromAsyncEnumerable<string, PageStore>(
			state,
			static (store, _) => ReadItemsAsync(store.Items),
			filter: static (_, item) => item.Contains('o', StringComparison.Ordinal));

		var page = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		page.Items.Should().Equal("one", "two");
		page.PageInfo.NextCursor.Should().Be("2");

		var second = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: page.PageInfo.NextCursor,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		second.Items.Should().Equal("four");
		second.PageInfo.HasMore.Should().BeFalse();
	}

	[TestMethod]
	[Description("ReplPageSource.FromItems can filter bounded in-memory data before applying the final page window.")]
	public async Task When_FromItemsUsesFilter_Then_PagesFilteredItems()
	{
		var source = ReplPageSource.FromItems(
			["one", "two", "three", "four"],
			static item => item.Contains('o', StringComparison.Ordinal));

		var page = await source.FetchAsync(
			new ReplPageRequest(
				PageSize: 2,
				Cursor: null,
				VisibleRowCapacityHint: null,
				AllRequested: false,
				Surface: ReplResultSurface.Console));

		page.Items.Should().Equal("one", "two");
		page.PageInfo.NextCursor.Should().Be("2");
		page.PageInfo.TotalCount.Should().Be(3);
	}

	[TestMethod]
	[Description("ReplPageRequest.Page copies request metadata and marks HasMore from the emitted cursor.")]
	public void When_RequestCreatesPage_Then_PageInfoUsesRequestAndNextCursor()
	{
		var request = new ReplPageRequest(
			PageSize: 5,
			Cursor: "start",
			VisibleRowCapacityHint: 10,
			AllRequested: false,
			Surface: ReplResultSurface.Console);

		var page = request.Page(["one"], nextCursor: "next", totalCount: 2);

		page.Items.Should().Equal("one");
		page.PageInfo.Cursor.Should().Be("start");
		page.PageInfo.NextCursor.Should().Be("next");
		page.PageInfo.TotalCount.Should().Be(2);
		page.PageInfo.PageSize.Should().Be(5);
		page.PageInfo.HasMore.Should().BeTrue();
	}

	[TestMethod]
	[Description("ReplPageInfo derives HasMore from NextCursor so manual construction cannot create divergent page metadata.")]
	public void When_PageInfoHasNoNextCursor_Then_HasMoreIsFalse()
	{
		var pageInfo = new ReplPageInfo(
			Cursor: "current",
			NextCursor: null,
			TotalCount: null,
			PageSize: 10);

		pageInfo.HasMore.Should().BeFalse();
	}

	[TestMethod]
	[Description("ReplPage reuses object arrays for UntypedItems instead of allocating another array.")]
	public void When_ReplPageItemsAreObjectArray_Then_UntypedItemsReusesArray()
	{
		object?[] items = ["one", 2];
		var page = new ReplPage<object?>(
			items,
			new ReplPageInfo(
				Cursor: null,
				NextCursor: null,
				TotalCount: items.Length,
				PageSize: items.Length));

		page.UntypedItems.Should().BeSameAs(items);
	}

	private static async IAsyncEnumerable<string> ReadItemsAsync(IEnumerable<string> items)
	{
		foreach (var item in items)
		{
			await Task.Yield();
			yield return item;
		}
	}

	private static async IAsyncEnumerable<int> ReadIntItemsAsync(IEnumerable<int> items)
	{
		foreach (var item in items)
		{
			await Task.Yield();
			yield return item;
		}
	}

	private static async IAsyncEnumerable<int> ReadUntilCancelledAsync(
		Action observeCancellation,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		while (true)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				observeCancellation();
			}

			cancellationToken.ThrowIfCancellationRequested();
			await Task.Yield();
			yield return 1;
		}
	}

	private sealed record PageStore(IReadOnlyList<string> Items);

	private sealed class SingleUseAsyncEnumerable<T>(IReadOnlyList<T> items) : IAsyncEnumerable<T>
	{
		private bool _used;

		public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
		{
			if (_used)
			{
				throw new InvalidOperationException("The stream is not replayable.");
			}

			_used = true;
			foreach (var item in items)
			{
				await Task.Yield();
				cancellationToken.ThrowIfCancellationRequested();
				yield return item;
			}
		}
	}
}
