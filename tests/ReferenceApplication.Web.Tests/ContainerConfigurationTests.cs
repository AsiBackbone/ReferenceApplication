using System.Text.RegularExpressions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Guards the container contract for explicit database migration: the runtime image never carries migration tooling,
/// and Docker Compose applies migrations to <c>ApplicationDatabase</c> before the web service starts.
/// </summary>
/// <remarks>
/// The CI container smoke test exercises this end to end; these checks fail fast in the ordinary test run when the
/// Dockerfile or Compose file drifts from that contract.
/// </remarks>
public sealed partial class ContainerConfigurationTests
{
    private const string _migrationsService = "referenceapplication.migrations";
    private const string _webService = "referenceapplication.web";

    /// <summary>
    /// Verifies that a plain <c>docker build</c> still produces the runtime image, and that only the opt-in
    /// <c>migrations</c> target contains the migration bundle.
    /// </summary>
    [Fact]
    public void Dockerfile_MigrationsTarget_IsSeparateFromRuntimeImage()
    {
        string dockerfile = RepositoryFiles.ReadAllText("Dockerfile");
        string[] stages = [.. StageRegex().Matches(dockerfile).Select(match => match.Groups["name"].Value)];
        Dictionary<string, string> stageBodies = SplitStages(dockerfile);

        Assert.Equal("final", stages[^1]);
        Assert.Contains("migrations", stages);
        Assert.Contains("ENTRYPOINT [\"./efbundle\"]", stageBodies["migrations"], StringComparison.Ordinal);
        Assert.Contains("USER $APP_UID", stageBodies["migrations"], StringComparison.Ordinal);
        Assert.Contains("--context ApplicationDbContext", stageBodies["migrations-build"], StringComparison.Ordinal);
        Assert.DoesNotContain("efbundle", stageBodies["final"], StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"ReferenceApplication.Web.dll\"]", stageBodies["final"], StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that Compose builds the migrations target, keeps the demo identity off while migrating, and starts the
    /// web service only after migration completes successfully against the shared data volume.
    /// </summary>
    [Fact]
    public void Compose_WebService_WaitsForSuccessfulMigrationOfApplicationDatabase()
    {
        string compose = RepositoryFiles.ReadAllText("docker-compose.yml");
        string migrations = GetServiceBlock(compose, _migrationsService);
        string web = GetServiceBlock(compose, _webService);

        Assert.Contains("target: migrations", migrations, StringComparison.Ordinal);
        Assert.Contains("<<: *application-environment", migrations, StringComparison.Ordinal);
        Assert.Contains("ReferenceApplication__DemoIdentity__Enabled: \"false\"", migrations, StringComparison.Ordinal);
        Assert.Contains("referenceapplication-data:/app/data", migrations, StringComparison.Ordinal);
        Assert.Contains("restart: \"no\"", migrations, StringComparison.Ordinal);

        Assert.DoesNotContain("target:", web, StringComparison.Ordinal);
        Assert.Contains("<<: *application-environment", web, StringComparison.Ordinal);
        Assert.Matches(
            new Regex($@"depends_on:\s+{Regex.Escape(_migrationsService)}:\s+condition: service_completed_successfully"),
            web);
        Assert.Contains("referenceapplication-data:/app/data", web, StringComparison.Ordinal);

        // Both services must resolve the same connection string, so it is defined once in the shared environment.
        Assert.Single(ApplicationDatabaseRegex().Matches(compose));
    }

    /// <summary>
    /// Verifies that the production-shaped override selects <c>Production</c> for both services, turns every
    /// Development-only component and trust relaxation off, and takes every secret and deployment-specific value from
    /// the environment or a file secret, never from the file itself.
    /// </summary>
    [Fact]
    public void ProductionCompose_ConfiguresBothServicesWithoutDevelopmentComponentsOrCheckedInSecrets()
    {
        string compose = RepositoryFiles.ReadAllText("docker-compose.production.yml");
        string migrations = GetServiceBlock(compose, _migrationsService);
        string web = GetServiceBlock(compose, _webService);

        Assert.Contains("ASPNETCORE_ENVIRONMENT: Production", compose, StringComparison.Ordinal);
        Assert.Contains("environment: *production-environment", migrations, StringComparison.Ordinal);
        Assert.Contains("<<: *production-environment", web, StringComparison.Ordinal);
        Assert.Contains("- dataprotection-certificate", migrations, StringComparison.Ordinal);
        Assert.Contains("- dataprotection-certificate", web, StringComparison.Ordinal);

        foreach (string component in new[]
        {
            "DemoIdentity", "GovernanceLoadDemonstration", "PolicySimulator", "DecisionReplay", "PipelineInspector",
            "FailureScenarios", "CapabilityTokens"
        })
        {
            Assert.Contains($"ReferenceApplication__{component}__Enabled: \"false\"", compose, StringComparison.Ordinal);
        }

        Assert.Contains("ReferenceApplication__ForwardedHeaders__ClearKnownNetworksAndProxies: \"false\"", compose, StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceApplication__DataProtection__KeyEncryptionCertificatePath: /run/secrets/dataprotection-certificate",
            compose,
            StringComparison.Ordinal);

        // Each of these must be required from the environment, so Compose refuses to start without it.
        foreach (string setting in new[]
        {
            "AllowedHosts",
            "ReferenceApplication__ForwardedHeaders__KnownNetworks__0",
            "ReferenceApplication__DataProtection__KeyEncryptionCertificatePassword",
            "ReferenceApplication__Authentication__Providers__OpenIdConnect__Authority",
            "ReferenceApplication__Authentication__Providers__OpenIdConnect__ClientId",
            "ReferenceApplication__Authentication__Providers__OpenIdConnect__ClientSecret",
            "file"
        })
        {
            Assert.Matches(new Regex($@"^\s*{Regex.Escape(setting)}: \$\{{[A-Z_]+:\?", RegexOptions.Multiline), compose);
        }
    }

    private static Dictionary<string, string> SplitStages(string dockerfile)
    {
        MatchCollection matches = StageRegex().Matches(dockerfile);
        Dictionary<string, string> bodies = new(StringComparer.Ordinal);

        for (int index = 0; index < matches.Count; index++)
        {
            int start = matches[index].Index;
            int end = index + 1 < matches.Count ? matches[index + 1].Index : dockerfile.Length;
            bodies[matches[index].Groups["name"].Value] = dockerfile[start..end];
        }

        return bodies;
    }

    private static string GetServiceBlock(string compose, string serviceName)
    {
        string header = $"\n  {serviceName}:\n";
        int start = compose.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Compose service '{serviceName}' was not found.");

        Match next = NextTopLevelOrServiceRegex().Match(compose, start + header.Length);
        return next.Success ? compose[start..next.Index] : compose[start..];
    }

    [GeneratedRegex(@"^FROM\s+\S+\s+AS\s+(?<name>\S+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex StageRegex();

    [GeneratedRegex(@"\n(?:  [^\s#][^\n]*:\n|[^\s#][^\n]*:\n)")]
    private static partial Regex NextTopLevelOrServiceRegex();

    [GeneratedRegex(@"^\s*ConnectionStrings__ApplicationDatabase:", RegexOptions.Multiline)]
    private static partial Regex ApplicationDatabaseRegex();
}
