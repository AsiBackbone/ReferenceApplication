using System.Text.RegularExpressions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the first-contact routing: the README's opening and five-minute tour, its feedback routes, and the issue
/// forms and chooser configuration under <c>.github/ISSUE_TEMPLATE</c>.
/// </summary>
public sealed partial class FeedbackRoutingDocumentationTests
{
    private const string _privateReportingUrl =
        "https://github.com/AsiBackbone/ReferenceApplication/security/advisories/new";

    /// <summary>
    /// Verifies that the repository front door uses its organization social image and links the core status badges to
    /// the owning workflows, documentation, security policy, and release surface.
    /// </summary>
    [Fact]
    public void Readme_UsesOrganizationSocialImageAndCoreBadges()
    {
        string readme = RepositoryFiles.ReadAllText("README.md");

        foreach (string expected in new[]
                 {
                     "https://raw.githubusercontent.com/AsiBackbone/ReferenceApplication/main/docs/images/asibackbone-social.png",
                     "actions/workflows/ci.yml/badge.svg",
                     "eng/code-coverage-floors.json",
                     "api.scorecard.dev/projects/github.com/AsiBackbone/ReferenceApplication/badge",
                     "actions/workflows/publish-docs.yml/badge.svg",
                     "https://asibackbone.github.io/ReferenceApplication/",
                     "](LICENSE.txt)",
                     "](SECURITY.md)",
                     "img.shields.io/github/v/release/AsiBackbone/ReferenceApplication"
                 })
        {
            Assert.Contains(expected, readme, StringComparison.Ordinal);
        }
    }

    private static readonly string[] _discussionCategoryUrls =
    [
        "https://github.com/orgs/AsiBackbone/discussions/categories/q-a",
        "https://github.com/orgs/AsiBackbone/discussions/categories/ideas",
        "https://github.com/orgs/AsiBackbone/discussions/categories/architecture-integration"
    ];

    private static readonly string[] _issueForms =
    [
        ".github/ISSUE_TEMPLATE/bug_report.yml",
        ".github/ISSUE_TEMPLATE/demonstration_request.yml",
        ".github/ISSUE_TEMPLATE/documentation.yml"
    ];

    private static readonly string[] _issueFormElementTypes =
        ["markdown", "input", "textarea", "dropdown", "checkboxes"];

    /// <summary>
    /// Verifies that the text before the Status section names the terms an evaluator is likely to search for.
    /// </summary>
    [Fact]
    public void Readme_OpeningNamesDiscoveryTerms()
    {
        string readme = RepositoryFiles.ReadAllText("README.md");
        int statusIndex = readme.IndexOf("\n## Status", StringComparison.Ordinal);
        Assert.True(statusIndex > 0, "README.md must keep its Status section.");
        string opening = readme[..statusIndex];

        foreach (string term in new[]
                 {
                     "ASP.NET Core reference application",
                     "NCAT",
                     "governance",
                     "policy enforcement",
                     "audit evidence",
                     "example, not a product",
                     "[five-minute tour](#five-minute-tour)"
                 })
        {
            Assert.Contains(term, opening, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Verifies that the five-minute tour names its one observable outcome, the denial contrast, and its non-claims.
    /// </summary>
    [Fact]
    public void Readme_FiveMinuteTourStatesOutcomeAndNonClaims()
    {
        string tour = ReadSection(RepositoryFiles.ReadAllText("README.md"), "## Five-minute tour");

        Assert.Contains("/governance-explorer", tour, StringComparison.Ordinal);
        Assert.Contains("**Create audited record**", tour, StringComparison.Ordinal);
        Assert.Contains("**Audit Completion**", tour, StringComparison.Ordinal);
        Assert.Contains("referenceapplication.capability.grant_required", tour, StringComparison.Ordinal);
        Assert.Contains("**What this shows:**", tour, StringComparison.Ordinal);
        Assert.Contains("**What it doesn't show:**", tour, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the README feedback section links each discussion category, the issue forms, and private
    /// vulnerability reporting.
    /// </summary>
    [Fact]
    public void Readme_FeedbackSectionLinksEveryRoute()
    {
        string feedback = ReadSection(RepositoryFiles.ReadAllText("README.md"), "## Feedback and community");

        foreach (string url in _discussionCategoryUrls)
        {
            Assert.Contains(url, feedback, StringComparison.Ordinal);
        }

        Assert.Contains("https://github.com/AsiBackbone/ReferenceApplication/issues/new/choose", feedback, StringComparison.Ordinal);
        Assert.Contains(_privateReportingUrl, feedback, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the issue chooser disables blank issues and routes questions, ideas, integration discussion, and
    /// vulnerabilities away from public Issues.
    /// </summary>
    [Fact]
    public void IssueChooser_DisablesBlankIssuesAndLinksRoutes()
    {
        string config = RepositoryFiles.ReadAllText(".github/ISSUE_TEMPLATE/config.yml");

        Assert.Matches(BlankIssuesDisabledRegex(), config);
        Assert.Contains(_privateReportingUrl, config, StringComparison.Ordinal);

        foreach (string url in _discussionCategoryUrls)
        {
            Assert.Contains(url, config, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Verifies the structure GitHub requires of each issue form: top-level metadata, a body of known element types,
    /// and unique element identifiers.
    /// </summary>
    /// <param name="relativePath">The issue form path.</param>
    [Theory]
    [MemberData(nameof(IssueForms))]
    public void IssueForm_HasRequiredStructure(string relativePath)
    {
        string form = RepositoryFiles.ReadAllText(relativePath);

        Assert.Matches(TopLevelKeyRegex("name"), form);
        Assert.Matches(TopLevelKeyRegex("description"), form);
        Assert.Matches(TopLevelKeyRegex("labels"), form);
        Assert.Matches(TopLevelKeyRegex("body"), form);
        Assert.DoesNotContain("\t", form, StringComparison.Ordinal);

        string[] types = [.. ElementTypeRegex().Matches(form).Select(static match => match.Groups["type"].Value)];
        Assert.NotEmpty(types);
        Assert.All(types, type => Assert.Contains(type, _issueFormElementTypes));
        Assert.Contains(types, static type => type != "markdown");

        string[] ids = [.. ElementIdRegex().Matches(form).Select(static match => match.Groups["id"].Value)];
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Verifies that the bug form keeps vulnerabilities out of public Issues and asks for redacted logs.
    /// </summary>
    [Fact]
    public void BugReportForm_RoutesSecurityPrivatelyAndRequiresRedaction()
    {
        string form = RepositoryFiles.ReadAllText(".github/ISSUE_TEMPLATE/bug_report.yml");

        Assert.Contains(_privateReportingUrl, form, StringComparison.Ordinal);
        Assert.Contains("id: run-mode", form, StringComparison.Ordinal);
        Assert.Contains("id: logs", form, StringComparison.Ordinal);
        Assert.Contains("This is not a suspected vulnerability.", form, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets the issue form paths for <see cref="IssueForm_HasRequiredStructure"/>.
    /// </summary>
    /// <returns>One row per issue form.</returns>
    public static TheoryData<string> IssueForms()
    {
        return [.. _issueForms];
    }

    private static string ReadSection(string markdown, string heading)
    {
        int start = markdown.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"README.md must contain the '{heading}' section.");

        int next = markdown.IndexOf("\n## ", start + heading.Length + 1, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }

    private static Regex TopLevelKeyRegex(string key)
    {
        return new Regex($"^{key}:", RegexOptions.Multiline);
    }

    [GeneratedRegex(@"^blank_issues_enabled:\s*false\s*$", RegexOptions.Multiline)]
    private static partial Regex BlankIssuesDisabledRegex();

    [GeneratedRegex(@"^  - type: (?<type>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ElementTypeRegex();

    [GeneratedRegex(@"^    id: (?<id>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ElementIdRegex();
}
