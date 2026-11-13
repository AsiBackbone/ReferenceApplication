using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Guards the recorded release-artifact signing decision (#129): the assessment states an explicit decision, the stable
/// release procedure and the documentation site reach it, and CI exercises the fail-closed guard that enforces it.
/// </summary>
public sealed class ReleaseArtifactSigningDocumentationTests
{
    private const string _assessmentPath = "docs/release-artifact-signing.md";
    private const string _guardPath = "scripts/Test-ReleaseArtifactSigning.ps1";

    /// <summary>
    /// Verifies that the assessment records the Deferred decision the guard parses, together with the inventory, the
    /// revisit triggers, and the separation from runtime governance evidence.
    /// </summary>
    [Fact]
    public void Assessment_RecordsDeferredDecisionWithInventoryAndRevisitTriggers()
    {
        string assessment = RepositoryFiles.ReadAllText(_assessmentPath);

        Assert.Contains("\n**Decision:** Deferred\n", assessment.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("## Inventory", assessment, StringComparison.Ordinal);
        Assert.Contains("## Threat model", assessment, StringComparison.Ordinal);
        Assert.Contains("## Revisit triggers", assessment, StringComparison.Ordinal);
        Assert.Contains("**zero attached assets**", assessment, StringComparison.Ordinal);
        Assert.Contains("**artifact provenance** only", assessment, StringComparison.Ordinal);
        Assert.Contains("./scripts/Test-ReleaseArtifactSigning.ps1 -Tag vX.Y.Z", assessment, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the stable release procedure runs the guard and links the assessment, and that the documentation
    /// site builds and lists the assessment page.
    /// </summary>
    [Fact]
    public void ReleasePolicyAndDocumentationSite_ReachAssessment()
    {
        string policy = RepositoryFiles.ReadAllText("docs/stable-release-policy-v1.md");

        Assert.Contains("](release-artifact-signing.md)", policy, StringComparison.Ordinal);
        Assert.Contains("./scripts/Test-ReleaseArtifactSigning.ps1 -Tag", policy, StringComparison.Ordinal);
        Assert.Contains("href: release-artifact-signing.md", RepositoryFiles.ReadAllText("docs/toc.yml"), StringComparison.Ordinal);
        Assert.Contains("\"release-artifact-signing.md\"", RepositoryFiles.ReadAllText("docs/docfx.json"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the guard exists and that CI runs its offline self-test, so the fail-closed paths stay exercised.
    /// </summary>
    [Fact]
    public void Guard_ExistsAndCiRunsSelfTest()
    {
        Assert.True(File.Exists(RepositoryFiles.GetPath(_guardPath)), $"{_guardPath} is missing.");
        Assert.Contains(
            "run: ./scripts/Test-ReleaseArtifactSigning.ps1 -SelfTest",
            RepositoryFiles.ReadAllText(".github/workflows/ci.yml"),
            StringComparison.Ordinal);
    }
}
