using System.Xml.Linq;
using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ProjectBoundaries
{
	[TestMethod]
	[Description("Regression guard: verifies Repl.Core stays dependency-free and does not reference external runtime packages or sibling projects.")]
	public void When_InspectingCoreProjectFile_Then_NoRuntimeDependenciesAreDeclared()
	{
		var repoRoot = FindRepositoryRoot();
		var coreProjectPath = Path.Combine(repoRoot, "src", "Repl.Core", "Repl.Core.csproj");
		File.Exists(coreProjectPath).Should().BeTrue();

		var project = XDocument.Load(coreProjectPath);
		var packageReferences = project
			.Descendants()
			.Where(node => string.Equals(node.Name.LocalName, "PackageReference", StringComparison.Ordinal))
			.ToArray();
		var projectReferences = project
			.Descendants()
			.Where(node => string.Equals(node.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
			.ToArray();

		packageReferences.Should().BeEmpty("Repl.Core must remain dependency-free at the project level.");
		projectReferences.Should().BeEmpty("Repl.Core must not depend on sibling projects.");
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			var solutionPath = Path.Combine(directory.FullName, "src", "Repl.slnx");
			if (File.Exists(solutionPath))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new InvalidOperationException("Unable to locate repository root from test output directory.");
	}
}
