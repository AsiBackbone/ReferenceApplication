using System.Security.Claims;
using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Evaluation;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Extensions;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Benchmarks;

/// <summary>
/// Measures the host-owned Step 4 constraints and decision policy through the production evaluator composition.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[JsonExporterAttribute.Full]
[SimpleJob(RuntimeMoniker.Net10_0, launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class GovernancePolicyBenchmarks
{
    private ServiceProvider _serviceProvider = null!;
    private IServiceScope _serviceScope = null!;
    private IGovernancePolicyEvaluator<GovernanceEvaluationContext> _evaluator = null!;
    private GovernanceEvaluationContext _administratorContext = null!;
    private GovernanceEvaluationContext _capabilityContext = null!;

    /// <summary>
    /// Builds the same scoped evaluator composition used by the web host.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ApplicationClaimTypes.Role, "administrator")],
                    authenticationType: "Benchmark"))
            }
        };

        ServiceCollection services = new();
        _ = services.AddLogging();
        _ = services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);
        _ = services.Configure<ApplicationAuthorizationOptions>(options =>
        {
            options.RoleClaimType = ApplicationClaimTypes.Role;
            options.AdministratorRoles = ["administrator"];
        });
        _ = services.AddApplicationAsiBackbone();

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        _serviceScope = _serviceProvider.CreateScope();
        _evaluator = _serviceScope.ServiceProvider
            .GetRequiredService<IGovernancePolicyEvaluator<GovernanceEvaluationContext>>();

        _administratorContext = CreateContext(
            typeof(AdministratorActionPolicy),
            typeof(ConsequentialActionPolicy));
        _capabilityContext = CreateContext(
            [typeof(CapabilityProtectedActionPolicy)],
            ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope);
    }

    /// <summary>
    /// Releases the scoped host composition after the benchmark run.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _serviceScope.Dispose();
        _serviceProvider.Dispose();
    }

    /// <summary>
    /// Measures an administrator constraint followed by the host consequential-action decision policy.
    /// </summary>
    /// <returns>The composed governance decision.</returns>
    [Benchmark(Description = "host_policy.administrator_and_consequential")]
    public ValueTask<GovernanceDecision> EvaluateAdministratorAndConsequentialAsync()
    {
        return _evaluator.EvaluateAsync(_administratorContext);
    }

    /// <summary>
    /// Measures the host capability-scope declaration constraint through the composed evaluator.
    /// </summary>
    /// <returns>The composed governance decision.</returns>
    [Benchmark(Description = "host_policy.capability_scope")]
    public ValueTask<GovernanceDecision> EvaluateCapabilityScopeAsync()
    {
        return _evaluator.EvaluateAsync(_capabilityContext);
    }

    private static GovernanceEvaluationContext CreateContext(
        params Type[] policyTypes)
    {
        return CreateContext(policyTypes, capabilityScope: null);
    }

    private static GovernanceEvaluationContext CreateContext(
        IReadOnlyList<Type> policyTypes,
        string? capabilityScope)
    {
        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["endpoint.policy_types"] = string.Join(
                ',',
                policyTypes.Select(static policyType => policyType.FullName ?? policyType.Name))
        };

        if (capabilityScope is not null)
        {
            metadata["endpoint.capability_scopes"] = capabilityScope;
        }

        return new GovernanceEvaluationContext(
            correlationId: "benchmark-correlation",
            policyVersion: ReferenceApplicationGovernancePolicySet.Version,
            policyHash: ReferenceApplicationGovernancePolicySet.PolicyHash,
            metadata: metadata);
    }
}
