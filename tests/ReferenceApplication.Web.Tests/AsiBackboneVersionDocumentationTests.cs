using System.Text.RegularExpressions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies that current AsiBackbone package documentation stays aligned with the centrally managed package versions
/// and the packages the host projects reference directly.
/// </summary>
/// <remarks>
/// Central package management holds every AsiBackbone package version in <c>Directory.Packages.props</c>, so it serves
/// as the reference package set and version. Project files determine which of those packages are direct references.
/// </remarks>
public sealed partial class AsiBackboneVersionDocumentationTests
{
    private static readonly string[] _projectFiles =
    [
        "src/ReferenceApplication.Web/ReferenceApplication.Web.csproj",
        "src/ReferenceApplication.Infrastructure/ReferenceApplication.Infrastructure.csproj",
        "tests/ReferenceApplication.Web.Tests/ReferenceApplication.Web.Tests.csproj",
    ];

    /// <summary>
    /// Verifies that every AsiBackbone package uses one version and that the documentation homepage names it.
    /// </summary>
    [Fact]
    public void CurrentPackageVersion_IsStatedOnTheDocumentationHomepage()
    {
        Dictionary<string, string> centrallyManagedPackages = GetCentrallyManagedPackages();
        string referencedVersion = Assert.Single(centrallyManagedPackages.Values.Distinct(StringComparer.Ordinal));

        Match homepage = HomepageVersionRegex().Match(RepositoryFiles.ReadAllText("docs/index.md"));
        Assert.True(homepage.Success, "docs/index.md does not state the current AsiBackbone version.");
        Assert.Equal(referencedVersion, homepage.Groups["version"].Value);
    }

    /// <summary>
    /// Verifies that Step 2 labels its current package section with the centrally managed version and lists every
    /// centrally pinned AsiBackbone package, including transitive pins, while covering every direct project reference.
    /// </summary>
    [Fact]
    public void WalkthroughStep2_CurrentPackageSetMatchesRepositoryReferences()
    {
        IReadOnlyDictionary<string, string> centrallyManagedPackages = GetCentrallyManagedPackages();
        string referencedVersion = Assert.Single(centrallyManagedPackages.Values.Distinct(StringComparer.Ordinal));

        Match currentSection = CurrentPackageSectionRegex()
            .Match(RepositoryFiles.ReadAllText("docs/walkthrough/02-add-asibackbone-packages.md"));
        Assert.True(currentSection.Success, "Step 2 does not contain a current AsiBackbone package section.");
        Assert.Equal(referencedVersion, currentSection.Groups["version"].Value);

        string[] documentedPackages = [.. CurrentPackageRowRegex()
            .Matches(currentSection.Groups["body"].Value)
            .Select(static match => match.Groups["package"].Value)
            .Order(StringComparer.Ordinal)];
        string[] centrallyManagedPackageNames = [.. centrallyManagedPackages.Keys.Order(StringComparer.Ordinal)];
        Assert.Equal(centrallyManagedPackageNames, documentedPackages);

        string[] directlyReferencedPackages = [.. _projectFiles
            .SelectMany(static path => PackageReferenceRegex()
                .Matches(RepositoryFiles.ReadAllText(path))
                .Select(static match => match.Groups["package"].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        foreach (string package in directlyReferencedPackages)
        {
            Assert.True(
                centrallyManagedPackages.ContainsKey(package),
                $"Direct package reference '{package}' is not centrally managed in Directory.Packages.props.");
            Assert.Contains(package, documentedPackages);
        }
    }

    private static Dictionary<string, string> GetCentrallyManagedPackages()
    {
        return PackageVersionRegex()
            .Matches(RepositoryFiles.ReadAllText("Directory.Packages.props"))
            .ToDictionary(
                static match => match.Groups["package"].Value,
                static match => match.Groups["version"].Value,
                StringComparer.Ordinal);
    }

    [GeneratedRegex(@"<PackageVersion Include=""(?<package>AsiBackbone\.[A-Za-z.]+)"" Version=""(?<version>\d+\.\d+\.\d+)""")]
    private static partial Regex PackageVersionRegex();

    [GeneratedRegex(@"<PackageReference Include=""(?<package>AsiBackbone\.[A-Za-z.]+)""")]
    private static partial Regex PackageReferenceRegex();

    [GeneratedRegex(@"^\| Governance framework \| \[AsiBackbone\]\([^)]+\) `(?<version>\d+\.\d+\.\d+)`", RegexOptions.Multiline)]
    private static partial Regex HomepageVersionRegex();

    [GeneratedRegex(@"^## Current package set — AsiBackbone `(?<version>\d+\.\d+\.\d+)`\n(?<body>.*?)(?=^## )", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CurrentPackageSectionRegex();

    [GeneratedRegex(@"^\| `(?<package>AsiBackbone\.[A-Za-z.]+)` \|", RegexOptions.Multiline)]
    private static partial Regex CurrentPackageRowRegex();
}
