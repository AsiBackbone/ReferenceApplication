using System.Text.RegularExpressions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies that every page stating the current NCAT baseline version agrees, so a baseline refresh cannot leave one
/// of them describing the previous release.
/// </summary>
/// <remarks>
/// The refresh guide holds the target version in its <c>$New</c> variable, and its procedure ends with that version as
/// the baseline, so it serves as the reference value.
/// </remarks>
public sealed partial class NcatBaselineDocumentationTests
{
    /// <summary>
    /// Verifies the README, documentation homepage, and Step 1 against the latest refresh target.
    /// </summary>
    [Fact]
    public void CurrentBaselineVersion_IsConsistentAcrossDocumentation()
    {
        string refreshTarget = ReadVersion(
            "docs/walkthrough/refresh-ncat-baseline.md",
            RefreshTargetRegex());

        Assert.Equal(refreshTarget, ReadVersion("README.md", ReadmeBaselineRegex()));
        Assert.Equal(refreshTarget, ReadVersion("docs/index.md", HomepageBaselineRegex()));
        Assert.Equal(refreshTarget, ReadVersion("docs/walkthrough/01-scaffold-from-ncat.md", StepOneBaselineTagRegex()));
    }

    private static string ReadVersion(string relativePath, Regex pattern)
    {
        Match match = pattern.Match(RepositoryFiles.ReadAllText(relativePath));
        Assert.True(match.Success, $"{relativePath} does not state the current NCAT baseline version.");

        return match.Groups["version"].Value;
    }

    [GeneratedRegex(@"^\$New = '(?<version>\d+\.\d+\.\d+)'$", RegexOptions.Multiline)]
    private static partial Regex RefreshTargetRegex();

    [GeneratedRegex(@"The current NCAT baseline is \*\*(?<version>\d+\.\d+\.\d+)\*\*")]
    private static partial Regex ReadmeBaselineRegex();

    [GeneratedRegex(@"^\| Host baseline \| \[NCAT\]\([^)]+\) `(?<version>\d+\.\d+\.\d+)`", RegexOptions.Multiline)]
    private static partial Regex HomepageBaselineRegex();

    [GeneratedRegex(@"current baseline tag \(currently `ncat-v(?<version>\d+\.\d+\.\d+)`\)")]
    private static partial Regex StepOneBaselineTagRegex();
}
