using System.Text.RegularExpressions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies that the documentation homepage states the AsiBackbone version the host references, so a package update
/// cannot leave it describing the previous release.
/// </summary>
/// <remarks>
/// Central package management holds every AsiBackbone package version in <c>Directory.Packages.props</c>, so it serves
/// as the reference value.
/// </remarks>
public sealed partial class AsiBackboneVersionDocumentationTests
{
    /// <summary>
    /// Verifies that every AsiBackbone package uses one version and that the documentation homepage names it.
    /// </summary>
    [Fact]
    public void CurrentPackageVersion_IsStatedOnTheDocumentationHomepage()
    {
        string[] packageVersions = [.. PackageVersionRegex()
            .Matches(RepositoryFiles.ReadAllText("Directory.Packages.props"))
            .Select(static match => match.Groups["version"].Value)
            .Distinct(StringComparer.Ordinal)];
        string referencedVersion = Assert.Single(packageVersions);

        Match homepage = HomepageVersionRegex().Match(RepositoryFiles.ReadAllText("docs/index.md"));
        Assert.True(homepage.Success, "docs/index.md does not state the current AsiBackbone version.");
        Assert.Equal(referencedVersion, homepage.Groups["version"].Value);
    }

    [GeneratedRegex(@"<PackageVersion Include=""AsiBackbone\.[A-Za-z.]+"" Version=""(?<version>\d+\.\d+\.\d+)""")]
    private static partial Regex PackageVersionRegex();

    [GeneratedRegex(@"^\| Governance framework \| \[AsiBackbone\]\([^)]+\) `(?<version>\d+\.\d+\.\d+)`", RegexOptions.Multiline)]
    private static partial Regex HomepageVersionRegex();
}
