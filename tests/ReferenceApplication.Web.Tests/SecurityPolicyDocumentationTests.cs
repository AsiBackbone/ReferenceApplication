using System.Text.RegularExpressions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the repository security policy: its reporting routes, its links, the documents that point to it, and its
/// supported-versions table against the release version in <c>Directory.Build.props</c>.
/// </summary>
public sealed partial class SecurityPolicyDocumentationTests
{
    private const string _securityPolicyPath = "SECURITY.md";
    private const string _securityPolicyUrl =
        "https://github.com/AsiBackbone/ReferenceApplication/blob/main/SECURITY.md";

    /// <summary>
    /// Verifies that the policy inherits the organization defaults by link and offers a private reporting route for
    /// each repository a finding can belong to.
    /// </summary>
    [Fact]
    public void SecurityPolicy_LinksOrganizationPolicyAndPrivateReportingRoutes()
    {
        string policy = RepositoryFiles.ReadAllText(_securityPolicyPath);

        Assert.Contains(
            "https://github.com/AsiBackbone/.github/blob/main/SECURITY.md",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/AsiBackbone/ReferenceApplication/security/advisories/new",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/AsiBackbone/AsiBackbone/security/advisories/new",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/AsiBackbone/NetCoreApplicationTemplate/security/advisories/new",
            policy,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that every relative link in the policy names an existing repository file and every in-page link names
    /// one of its headings.
    /// </summary>
    [Fact]
    public void SecurityPolicy_LocalLinksResolve()
    {
        string policy = RepositoryFiles.ReadAllText(_securityPolicyPath);
        HashSet<string> headingAnchors = [.. HeadingRegex()
            .Matches(policy)
            .Select(static match => ToAnchor(match.Groups["heading"].Value))];

        string[] targets = [.. LinkTargetRegex()
            .Matches(policy)
            .Select(static match => match.Groups["target"].Value)
            .Where(static target => !target.StartsWith("https://", StringComparison.Ordinal))];
        Assert.NotEmpty(targets);

        foreach (string target in targets)
        {
            if (target.StartsWith('#'))
            {
                Assert.Contains(target[1..], headingAnchors);
                continue;
            }

            string path = target.Split('#')[0];
            Assert.True(File.Exists(RepositoryFiles.GetPath(path)), $"SECURITY.md links to missing file '{path}'.");
        }
    }

    /// <summary>
    /// Verifies that the README and the stable release policy link to the security policy. The release policy is
    /// published on the documentation site, outside the repository tree, so it links by absolute URL.
    /// </summary>
    [Fact]
    public void ReadmeAndStableReleasePolicy_LinkSecurityPolicy()
    {
        Assert.Contains("](SECURITY.md)", RepositoryFiles.ReadAllText("README.md"), StringComparison.Ordinal);
        Assert.Contains(
            $"]({_securityPolicyUrl})",
            RepositoryFiles.ReadAllText("docs/stable-release-policy-v1.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the supported-versions table has a row for the current release's major line, and that a stable
    /// release does not leave that row describing the line as unreleased.
    /// </summary>
    [Fact]
    public void SupportedVersions_ListCurrentReleaseLine()
    {
        string props = RepositoryFiles.ReadAllText("Directory.Build.props");
        Match prefix = VersionPrefixRegex().Match(props);
        Assert.True(prefix.Success, "Directory.Build.props does not define VersionPrefix.");
        bool isStable = string.IsNullOrWhiteSpace(VersionSuffixRegex().Match(props).Groups["suffix"].Value);

        string majorLine = $"`{prefix.Groups["major"].Value}.x`";
        string[] rows = [.. TableRowRegex()
            .Matches(RepositoryFiles.ReadAllText(_securityPolicyPath))
            .Select(static match => match.Value)
            .Where(row => FirstCell(row).Contains(majorLine, StringComparison.Ordinal))];

        Assert.True(rows.Length > 0, $"SECURITY.md has no supported-versions row for the {majorLine} line.");

        if (isStable)
        {
            Assert.Contains(rows, static row => !row.Contains("not yet released", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string FirstCell(string tableRow)
    {
        return tableRow.Split('|', StringSplitOptions.TrimEntries)[1];
    }

    private static string ToAnchor(string heading)
    {
        return NonAnchorCharacterRegex().Replace(heading.Trim().ToLowerInvariant(), string.Empty).Replace(' ', '-');
    }

    [GeneratedRegex(@"^#{2,6} (?<heading>.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)\)")]
    private static partial Regex LinkTargetRegex();

    [GeneratedRegex(@"[^a-z0-9 \-]")]
    private static partial Regex NonAnchorCharacterRegex();

    [GeneratedRegex(@"^\|[^\n]+\|$", RegexOptions.Multiline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"<VersionPrefix>(?<major>\d+)\.\d+\.\d+</VersionPrefix>")]
    private static partial Regex VersionPrefixRegex();

    [GeneratedRegex(@"<VersionSuffix>(?<suffix>[^<]*)</VersionSuffix>")]
    private static partial Regex VersionSuffixRegex();
}
