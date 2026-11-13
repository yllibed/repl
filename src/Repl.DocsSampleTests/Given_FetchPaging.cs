using System.Text;
using DocsMcpSample.Search;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_FetchPaging
{
	[TestMethod]
	[Description("A bounded page preserves the fetched reference and reports a UTF-16 continuation offset.")]
	public void When_ARequestedPageHasMoreText_Then_ItReturnsTheRequestedSlice()
	{
		var page = FetchPaging.Create("section", "doc", "Guide", "guide.md", new string((char)97, 300), 100, 100, "next", "generation");

		page.Id.Should().Be("section");
		page.DocumentId.Should().Be("doc");
		page.Source.Should().Be("guide.md");
		page.Title.Should().Be("Guide");
		page.Generation.Should().Be("generation");
		page.Text.Should().Be(new string((char)97, 100));
		page.Offset.Should().Be(100);
		page.NextOffset.Should().Be(200);
		page.Truncated.Should().BeTrue();
	}

	[TestMethod]
	[DataRow(100)]
	[DataRow(101)]
	[DataRow(20000)]
	[Description("Following offsets reconstructs more than 20,000 UTF-16 characters without splitting or losing surrogate pairs.")]
	public void When_PagingLongUnicodeText_Then_ConcatenatingPagesReconstructsItExactly(int maxChars)
	{
		var text = new string('a', 19999) + "😀" + string.Concat(Enumerable.Repeat("abc🌍", 5000));
		var reconstructed = new StringBuilder();
		var offset = 0;
		var pages = 0;
		while (true)
		{
			var page = FetchPaging.Create("doc", "doc", "Guide", "guide.md", text, maxChars, offset);
			page.Offset.Should().Be(offset);
			page.Text.Length.Should().BeInRange(1, maxChars);
			char.IsLowSurrogate(page.Text[0]).Should().BeFalse();
			char.IsHighSurrogate(page.Text[^1]).Should().BeFalse();
			page.NextId.Should().BeNull();
			reconstructed.Append(page.Text);
			(++pages).Should().BeLessThan(text.Length);
			if (page.NextOffset is not { } nextOffset)
			{
				page.Truncated.Should().BeFalse();
				break;
			}
			page.Truncated.Should().BeTrue();
			nextOffset.Should().Be(offset + page.Text.Length);
			offset = nextOffset;
		}

		pages.Should().BeGreaterThan(1);
		reconstructed.ToString().Should().Be(text);
	}

	[TestMethod]
	[Description("The page end moves back by one UTF-16 code unit when the budget would split a surrogate pair.")]
	public void When_ThePageBoundarySplitsASurrogatePair_Then_ThePairStartsTheNextPage()
	{
		var text = new string('a', 99) + "😀end";
		var first = FetchPaging.Create("section", "doc", "Guide", "guide.md", text, 100);
		var second = FetchPaging.Create("section", "doc", "Guide", "guide.md", text, 100, first.NextOffset!.Value);

		first.Text.Should().Be(new string('a', 99));
		first.NextOffset.Should().Be(99);
		second.Text.Should().Be("😀end");
		second.NextOffset.Should().BeNull();
	}

	[TestMethod]
	[DataRow(-1)]
	[DataRow(6)]
	[DataRow(int.MaxValue)]
	[Description("Offsets outside the text produce an actionable argument error rather than substring failures.")]
	public void When_TheOffsetIsOutOfRange_Then_ItIsRejected(int offset)
	{
		var fetch = () => FetchPaging.Create("doc", "doc", "Guide", "guide.md", "hello", 100, offset);

		var exception = fetch.Should().Throw<ArgumentException>().Which;
		exception.ParamName.Should().Be("offset");
		exception.Message.Should().Match("*UTF-16*0*5*");
	}

	[TestMethod]
	[Description("An offset between a high and low surrogate is rejected rather than returning invalid UTF-16.")]
	public void When_TheOffsetSplitsASurrogatePair_Then_ItIsRejected()
	{
		var fetch = () => FetchPaging.Create("doc", "doc", "Guide", "guide.md", "a😀b", 100, 2);

		var exception = fetch.Should().Throw<ArgumentException>().Which;
		exception.ParamName.Should().Be("offset");
		exception.Message.Should().Contain("surrogate pair");
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("hello😀")]
	[Description("An offset equal to the text length returns an empty terminal page, including for empty text.")]
	public void When_TheOffsetIsAtTheEnd_Then_ThePageIsEmpty(string text)
	{
		var page = FetchPaging.Create("doc", "doc", "Guide", "guide.md", text, 100, text.Length);

		page.Offset.Should().Be(text.Length);
		page.Text.Should().BeEmpty();
		page.NextOffset.Should().BeNull();
		page.NextId.Should().BeNull();
		page.Truncated.Should().BeFalse();
	}

	[TestMethod]
	[Description("The sibling section is exposed only after all characters of the current section have been fetched.")]
	public void When_ASectionHasASibling_Then_OnlyItsFinalPageReturnsNextId()
	{
		var text = new string('a', 200);
		var first = FetchPaging.Create("section", "doc", "Guide", "guide.md", text, 100, 0, "sibling");
		var last = FetchPaging.Create("section", "doc", "Guide", "guide.md", text, 100, 100, "sibling");
		var end = FetchPaging.Create("section", "doc", "Guide", "guide.md", text, 100, text.Length, "sibling");

		first.NextId.Should().BeNull();
		last.NextId.Should().Be("sibling");
		last.NextOffset.Should().BeNull();
		last.Truncated.Should().BeFalse();
		end.NextId.Should().Be("sibling");
	}

	[TestMethod]
	[Description("Whole-document fetches never expose section siblings, even when supplied by a caller.")]
	public void When_FetchingADocument_Then_ItHasNoSiblingContinuation()
	{
		var page = FetchPaging.Create("doc", "doc", "Guide", "guide.md", "hello", 100, 0, "sibling");

		page.NextId.Should().BeNull();
	}

	[TestMethod]
	[Description("A section without a supplied same-source, same-anchor sibling ends without a next identifier.")]
	public void When_ASectionHasNoSibling_Then_ItHasNoIdentifierContinuation()
	{
		var page = FetchPaging.Create("section", "doc", "Guide", "guide.md", "hello", 100);

		page.NextId.Should().BeNull();
		page.NextOffset.Should().BeNull();
	}
}
