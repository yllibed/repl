using System.Runtime.CompilerServices;

namespace Repl.Tests;

[TestClass]
public sealed class Given_TupleDecomposer
{
	[TestMethod]
	[Description("Detects a two-element ValueTuple as a tuple result.")]
	public void When_ValueIsTwoElementTuple_Then_DetectedAsTuple()
	{
		object value = (1, "hello");

		var result = TupleDecomposer.IsTupleResult(value, out var tuple);

		result.Should().BeTrue();
		tuple.Length.Should().Be(2);
		tuple[0].Should().Be(1);
		tuple[1].Should().Be("hello");
	}

	[TestMethod]
	[Description("Detects a three-element ValueTuple as a tuple result.")]
	public void When_ValueIsThreeElementTuple_Then_DetectedAsTuple()
	{
		object value = ("a", "b", "c");

		var result = TupleDecomposer.IsTupleResult(value, out var tuple);

		result.Should().BeTrue();
		tuple.Length.Should().Be(3);
	}

	[TestMethod]
	[Description("Rejects null as not a tuple.")]
	public void When_ValueIsNull_Then_NotDetectedAsTuple()
	{
		var result = TupleDecomposer.IsTupleResult(value: null, tuple: out _);

		result.Should().BeFalse();
	}

	[TestMethod]
	[Description("Rejects a plain string as not a tuple.")]
	public void When_ValueIsString_Then_NotDetectedAsTuple()
	{
		var result = TupleDecomposer.IsTupleResult("hello", out _);

		result.Should().BeFalse();
	}

	[TestMethod]
	[Description("Rejects a reference Tuple<> as not a tuple result.")]
	public void When_ValueIsReferenceTuple_Then_NotDetectedAsTuple()
	{
		var result = TupleDecomposer.IsTupleResult(Tuple.Create(1, 2), out _);

		result.Should().BeFalse();
	}

	[TestMethod]
	[Description("Handles an eight-element ValueTuple (which uses nested TRest internally).")]
	public void When_ValueIsEightElementTuple_Then_DetectedAsTuple()
	{
		object value = (1, 2, 3, 4, 5, 6, 7, 8);

		var result = TupleDecomposer.IsTupleResult(value, out var tuple);

		result.Should().BeTrue();
		tuple.Length.Should().Be(8);
		tuple[7].Should().Be(8);
	}
}
