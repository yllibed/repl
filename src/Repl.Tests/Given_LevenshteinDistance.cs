using System.Reflection;

namespace Repl.Tests;

[TestClass]
public sealed class Given_LevenshteinDistance
{
	[TestMethod]
	[Description("Regression guard: verifies classic Levenshtein examples keep expected distances after optimized implementation.")]
	public void When_ComputingDistance_Then_KnownPairsMatchExpectedValues()
	{
		Compute("kitten", "sitting").Should().Be(3);
		Compute("flaw", "lawn").Should().Be(2);
		Compute("hello", "helo").Should().Be(1);
		Compute("abc", "abc").Should().Be(0);
		Compute(string.Empty, "abc").Should().Be(3);
		Compute("abc", string.Empty).Should().Be(3);
	}

	[TestMethod]
	[Description("Regression guard: verifies Levenshtein distance remains symmetric for command suggestion computations.")]
	public void When_ComputingDistance_Then_DistanceIsSymmetric()
	{
		var leftToRight = Compute("suggestion", "sugestion");
		var rightToLeft = Compute("sugestion", "suggestion");

		leftToRight.Should().Be(rightToLeft);
	}

	private static int Compute(string source, string target)
	{
		var method = typeof(CoreReplApp).GetMethod(
			"ComputeLevenshteinDistance",
			BindingFlags.Static | BindingFlags.NonPublic);
		method.Should().NotBeNull();
		return (int)method!.Invoke(obj: null, parameters: [source, target])!;
	}
}
