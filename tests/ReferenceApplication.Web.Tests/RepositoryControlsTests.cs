using System.Text.Json;
using System.Text.RegularExpressions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies that the versioned <c>main</c> branch ruleset requires every check the CI workflow produces, so a new CI
/// job cannot be added without also blocking merges on its failure.
/// </summary>
public sealed partial class RepositoryControlsTests
{
    private const string _ciWorkflowPath = ".github/workflows/ci.yml";
    private const string _mainRulesetPath = "eng/repository-controls/main-branch-ruleset.json";
    private const int _gitHubActionsIntegrationId = 15368;

    /// <summary>
    /// Verifies that each CI job name is a required status check on <c>main</c>.
    /// </summary>
    [Fact]
    public void MainRuleset_RequiresEveryCiJob()
    {
        string[] jobNames = [.. JobNameRegex()
            .Matches(RepositoryFiles.ReadAllText(_ciWorkflowPath))
            .Select(match => match.Groups["name"].Value.Trim())];
        HashSet<string> requiredChecks = [.. ReadRequiredChecks().Select(check => check.Context)];

        Assert.Contains("Container smoke test", jobNames);
        Assert.All(jobNames, jobName => Assert.True(
            requiredChecks.Contains(jobName),
            $"CI job '{jobName}' is not a required status check in {_mainRulesetPath}."));
    }

    /// <summary>
    /// Verifies that required checks are unique and bound to the GitHub Actions app, so another integration cannot
    /// satisfy them by posting a status with the same name.
    /// </summary>
    [Fact]
    public void MainRuleset_RequiredChecks_AreUniqueAndBoundToGitHubActions()
    {
        List<(string Context, int? IntegrationId)> checks = ReadRequiredChecks();

        Assert.NotEmpty(checks);
        Assert.Equal(checks.Count, checks.Select(check => check.Context).Distinct(StringComparer.Ordinal).Count());
        Assert.All(checks, check => Assert.Equal(_gitHubActionsIntegrationId, check.IntegrationId));
    }

    private static List<(string Context, int? IntegrationId)> ReadRequiredChecks()
    {
        using var ruleset = JsonDocument.Parse(RepositoryFiles.ReadAllText(_mainRulesetPath));

        JsonElement statusChecks = ruleset.RootElement
            .GetProperty("rules")
            .EnumerateArray()
            .Single(rule => rule.GetProperty("type").GetString() == "required_status_checks")
            .GetProperty("parameters")
            .GetProperty("required_status_checks");

        return [.. statusChecks.EnumerateArray().Select(check => (
            check.GetProperty("context").GetString() ?? string.Empty,
            check.TryGetProperty("integration_id", out JsonElement integrationId) ? integrationId.GetInt32() : (int?)null))];
    }

    // Job names are the four-space-indented `name:` keys directly under each job; step names are indented further.
    [GeneratedRegex(@"^    name:\s*(?<name>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex JobNameRegex();
}
