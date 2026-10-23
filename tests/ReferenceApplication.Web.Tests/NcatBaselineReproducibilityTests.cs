using System.Text.Json;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the inputs and wiring of the <c>ncat-baseline</c> reproducibility check, so the generation manifest, the
/// workflow, and the refresh guide cannot drift apart.
/// </summary>
/// <remarks>
/// The check itself regenerates the scaffold from NuGet, which these tests do not do. The workflow runs it, after the
/// script's own self-test, against the real baseline.
/// </remarks>
public sealed class NcatBaselineReproducibilityTests
{
    private const string _manifestPath = "eng/ncat-baseline.json";
    private const string _scriptPath = "scripts/Test-NcatBaselineReproducibility.ps1";
    private const string _workflowPath = ".github/workflows/ncat-baseline-reproducibility.yml";
    private const string _refreshGuidePath = "docs/walkthrough/refresh-ncat-baseline.md";

    /// <summary>
    /// Verifies that the manifest names the template and application and passes every Step 1 option explicitly, so a
    /// changed template default cannot change the generation inputs.
    /// </summary>
    [Fact]
    public void Manifest_RecordsExplicitGenerationInputs()
    {
        using JsonDocument manifest = JsonDocument.Parse(RepositoryFiles.ReadAllText(_manifestPath));
        JsonElement root = manifest.RootElement;

        Assert.Equal("NetCoreApplicationTemplate", root.GetProperty("templatePackage").GetString());
        Assert.Equal("netcoreapp-template", root.GetProperty("templateShortName").GetString());
        Assert.Equal("ReferenceApplication", root.GetProperty("name").GetString());
        Assert.Equal("origin/ncat-baseline", root.GetProperty("baselineRef").GetString());
        Assert.Equal("ncat-v", root.GetProperty("tagPrefix").GetString());

        Dictionary<string, string?> options = root.GetProperty("options")
            .EnumerateObject()
            .ToDictionary(
                static option => option.Name,
                static option => option.Value.GetString(),
                StringComparer.Ordinal);
        Assert.Equal("cookie", options["authProvider"]);
        Assert.Equal("sqlite", options["dbProvider"]);
        Assert.Equal("true", options["skipRestore"]);
    }

    /// <summary>
    /// Verifies that the evidence sample in the refresh guide shows the generation command the manifest produces.
    /// </summary>
    [Fact]
    public void RefreshGuide_EvidenceSampleMatchesManifest()
    {
        using JsonDocument manifest = JsonDocument.Parse(RepositoryFiles.ReadAllText(_manifestPath));
        JsonElement root = manifest.RootElement;
        IEnumerable<string> options = root.GetProperty("options")
            .EnumerateObject()
            .Select(static option => $"--{option.Name} {option.Value.GetString()}");
        string shortName = root.GetProperty("templateShortName").GetString() ?? string.Empty;
        string name = root.GetProperty("name").GetString() ?? string.Empty;
        string generation = $"dotnet new {shortName} --name {name} {string.Join(' ', options)}";

        string guide = RepositoryFiles.ReadAllText(_refreshGuidePath);
        Assert.Contains($"Generation:        {generation}", guide, StringComparison.Ordinal);
        Assert.Contains("## Verify reproducibility", guide, StringComparison.Ordinal);
        Assert.Contains("./scripts/Test-NcatBaselineReproducibility.ps1", guide, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the workflow self-tests the script before running it, has the full history the baseline branch
    /// and tags need, reruns when the check changes, and passes manual inputs through the environment rather than
    /// expanding them into the script.
    /// </summary>
    [Fact]
    public void Workflow_SelfTestsThenVerifiesTheBaseline()
    {
        Assert.True(File.Exists(RepositoryFiles.GetPath(_scriptPath)), $"{_scriptPath} is missing.");
        string workflow = RepositoryFiles.ReadAllText(_workflowPath);

        int selfTest = workflow.IndexOf($"./{_scriptPath} -SelfTest", StringComparison.Ordinal);
        int verify = workflow.IndexOf(
            $"./{_scriptPath} -Ref $env:BASELINE_REF -TemplateVersion $env:TEMPLATE_VERSION",
            StringComparison.Ordinal);
        Assert.True(selfTest >= 0, "The workflow does not self-test the reproducibility script.");
        Assert.True(verify > selfTest, "The workflow does not verify the baseline after the self-test.");

        Assert.Contains("fetch-depth: 0", workflow, StringComparison.Ordinal);
        Assert.Contains("BASELINE_REF: ${{ inputs.ref }}", workflow, StringComparison.Ordinal);
        Assert.Contains("TEMPLATE_VERSION: ${{ inputs.template_version }}", workflow, StringComparison.Ordinal);
        Assert.Contains($"- '{_manifestPath}'", workflow, StringComparison.Ordinal);
        Assert.Contains($"- '{_scriptPath}'", workflow, StringComparison.Ordinal);
        Assert.Contains($"- '{_workflowPath}'", workflow, StringComparison.Ordinal);
    }
}
