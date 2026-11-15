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
    private const string _coverageFloorsPath = "eng/code-coverage-floors.json";
    private const string _linkWorkflowPath = ".github/workflows/link-validation.yml";
    private const string _versionWorkflowPath = ".github/workflows/version-consistency.yml";
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

    /// <summary>
    /// Verifies that the cross-repository alignment controls remain wired to pull requests and scheduled link review,
    /// that the deterministic version validator self-tests before checking the repository, and that ownership routes
    /// every path to the active maintainer without claiming independent approval.
    /// </summary>
    [Fact]
    public void AlignmentControls_AreWiredOwnedAndRequiredWhereApplicable()
    {
        string linkWorkflow = RepositoryFiles.ReadAllText(_linkWorkflowPath);
        string versionWorkflow = RepositoryFiles.ReadAllText(_versionWorkflowPath);
        string codeOwners = RepositoryFiles.ReadAllText(".github/CODEOWNERS");
        string lychee = RepositoryFiles.ReadAllText("lychee.toml");
        HashSet<string> requiredChecks = [.. ReadRequiredChecks().Select(check => check.Context)];

        Assert.Contains("pull_request:\n", linkWorkflow, StringComparison.Ordinal);
        Assert.Contains("schedule:\n", linkWorkflow, StringComparison.Ordinal);
        Assert.Contains("lycheeverse/lychee-action@e7477775783ea5526144ba13e8db5eec57747ce8", linkWorkflow, StringComparison.Ordinal);
        Assert.Contains("--offline", linkWorkflow, StringComparison.Ordinal);
        Assert.Contains("./docs/_site/**/*.html", linkWorkflow, StringComparison.Ordinal);
        Assert.Contains("security/advisories/new", lychee, StringComparison.Ordinal);

        int selfTest = versionWorkflow.IndexOf(
            "./scripts/Validate-VersionConsistency.ps1 -SelfTest",
            StringComparison.Ordinal);
        int validation = versionWorkflow.LastIndexOf(
            "./scripts/Validate-VersionConsistency.ps1",
            StringComparison.Ordinal);
        Assert.True(selfTest >= 0, "The version-consistency workflow does not self-test its validator.");
        Assert.True(validation > selfTest, "Repository version validation must run after the validator self-test.");
        Assert.Contains("Validate version consistency", requiredChecks);

        Assert.Contains("* @cdcavell", codeOwners, StringComparison.Ordinal);
        Assert.Contains("/src/ @cdcavell", codeOwners, StringComparison.Ordinal);
        Assert.Contains("/tests/ @cdcavell", codeOwners, StringComparison.Ordinal);
        Assert.Contains("/docs/ @cdcavell", codeOwners, StringComparison.Ordinal);
        Assert.Contains("/.github/ @cdcavell", codeOwners, StringComparison.Ordinal);
        Assert.Contains("/eng/repository-controls/ @cdcavell", codeOwners, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the required build-and-test job collects coverage with the referenced coverlet.MTP extension,
    /// excludes generated migrations, self-tests and runs the coverage gate, and publishes the report even when the
    /// gate fails, so the package reference is never left unused and a coverage regression fails the job.
    /// </summary>
    [Fact]
    public void Ci_BuildAndTest_CollectsGatesAndPublishesCoverage()
    {
        string workflow = RepositoryFiles.ReadAllText(_ciWorkflowPath);
        string testProject = RepositoryFiles.ReadAllText(
            "tests/ReferenceApplication.Web.Tests/ReferenceApplication.Web.Tests.csproj");

        Assert.Contains("<PackageReference Include=\"coverlet.MTP\">", testProject, StringComparison.Ordinal);
        Assert.Contains("--coverlet\n", workflow, StringComparison.Ordinal);
        Assert.Contains("--coverlet-output-format cobertura", workflow, StringComparison.Ordinal);
        Assert.Contains("--coverlet-exclude-by-file \"**/Migrations/*.cs\"", workflow, StringComparison.Ordinal);
        Assert.Contains("--results-directory TestResults/coverage", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "./scripts/Write-CoverageSummary.ps1 -ReportDirectory TestResults/coverage",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("./scripts/Write-CoverageSummary.ps1 -SelfTest", workflow, StringComparison.Ordinal);
        Assert.Contains("name: Summarize and gate coverage", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "if: ${{ !cancelled() && steps.test-with-coverage.outcome == 'success' }}",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("name: coverage-report", workflow, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", workflow, StringComparison.Ordinal);
        Assert.True(File.Exists(RepositoryFiles.GetPath("scripts/Write-CoverageSummary.ps1")));
        Assert.True(File.Exists(RepositoryFiles.GetPath(_coverageFloorsPath)));

        Assert.DoesNotContain("not gated", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("without a threshold gate", RepositoryFiles.ReadAllText("README.md"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the coverage floors cover exactly the production projects under <c>src</c>, so neither production
    /// assembly can regress behind a well-covered one and a new production project cannot ship without a floor.
    /// </summary>
    [Fact]
    public void CoverageFloors_FloorEveryProductionAssemblyWithinBounds()
    {
        using var floors = JsonDocument.Parse(RepositoryFiles.ReadAllText(_coverageFloorsPath));
        JsonElement root = floors.RootElement;

        string[] expectedProductionAssemblies = ["ReferenceApplication.Infrastructure", "ReferenceApplication.Web"];
        string[] productionAssemblies = [.. Directory
            .EnumerateFiles(RepositoryFiles.GetPath("src"), "*.csproj", SearchOption.AllDirectories)
            .Select(projectPath => Path.GetFileNameWithoutExtension(projectPath.AsSpan()).ToString())
            .Order(StringComparer.Ordinal)];
        string[] flooredAssemblies = [.. root
            .GetProperty("assemblies")
            .EnumerateObject()
            .Select(assembly => assembly.Name)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(expectedProductionAssemblies, productionAssemblies);
        Assert.Equal(productionAssemblies, flooredAssemblies);

        JsonElement tolerance = root.GetProperty("tolerance");
        Assert.InRange(tolerance.GetProperty("total").GetDecimal(), 0m, 2m);
        Assert.InRange(tolerance.GetProperty("assembly").GetDecimal(), 0m, 2m);

        AssertBaselinesInRange("total", root.GetProperty("total"));
        foreach (JsonProperty assembly in root.GetProperty("assemblies").EnumerateObject())
        {
            AssertBaselinesInRange(assembly.Name, assembly.Value);
        }
    }

    private static void AssertBaselinesInRange(string scope, JsonElement baseline)
    {
        foreach (string kind in new[] { "line", "branch" })
        {
            decimal value = baseline.GetProperty(kind).GetDecimal();
            Assert.True(value is > 0m and <= 100m, $"The {kind} baseline for {scope} must be above 0 and at most 100.");
        }
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
