using System.Text.Json;
using System.Text.RegularExpressions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies that every page stating the current NCAT baseline version agrees, so a baseline refresh cannot leave one
/// of them describing the previous release.
/// </summary>
/// <remarks>
/// The generation manifest is the machine-readable record of the adopted baseline. Refresh-guide version variables
/// belong to a historical worked example and must not be treated as the current baseline.
/// </remarks>
public sealed partial class NcatBaselineDocumentationTests
{
    private const string _manifestPath = "eng/ncat-baseline.json";

    /// <summary>
    /// Verifies the README, documentation homepage, and Step 1 against the adopted baseline in the manifest.
    /// </summary>
    [Fact]
    public void CurrentBaselineVersion_IsConsistentAcrossDocumentation()
    {
        using var manifest = JsonDocument.Parse(RepositoryFiles.ReadAllText(_manifestPath));
        string baselineVersion = manifest.RootElement.GetProperty("version").GetString() ?? string.Empty;
        Assert.Matches(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", baselineVersion);

        Assert.Equal(baselineVersion, ReadVersion("README.md", ReadmeBaselineRegex()));
        Assert.Equal(baselineVersion, ReadVersion("docs/index.md", HomepageBaselineRegex()));
        Assert.Equal(baselineVersion, ReadVersion("docs/walkthrough/01-scaffold-from-ncat.md", StepOneBaselineTagRegex()));
    }

    private static string ReadVersion(string relativePath, Regex pattern)
    {
        Match match = pattern.Match(RepositoryFiles.ReadAllText(relativePath));
        Assert.True(match.Success, $"{relativePath} does not state the current NCAT baseline version.");

        return match.Groups["version"].Value;
    }

    [GeneratedRegex(@"The current NCAT baseline is \*\*(?<version>\d+\.\d+\.\d+)\*\*")]
    private static partial Regex ReadmeBaselineRegex();

    [GeneratedRegex(@"^\| Host baseline \| \[NCAT\]\([^)]+\) `(?<version>\d+\.\d+\.\d+)`", RegexOptions.Multiline)]
    private static partial Regex HomepageBaselineRegex();

    [GeneratedRegex(@"current baseline tag \(currently `ncat-v(?<version>\d+\.\d+\.\d+)`\)")]
    private static partial Regex StepOneBaselineTagRegex();
}
