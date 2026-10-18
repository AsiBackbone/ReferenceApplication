using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.DependencyInjection;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Evaluation;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Provides the Reference Application's AsiBackbone service composition.
/// </summary>
public static class AsiBackboneServiceExtensions
{
    /// <summary>
    /// Adds AsiBackbone's ASP.NET Core integration and maps the application's normalized identity claims to a
    /// governance actor.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection so calls can be chained.</returns>
    public static IServiceCollection AddApplicationAsiBackbone(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAsiBackboneAspNetCore();
        services.Configure<HttpGovernanceActorContextOptions>(options =>
        {
            options.ActorIdClaimTypes =
            [
                ApplicationClaimTypes.Subject,
                .. HttpGovernanceActorContextOptions.DefaultActorIdClaimTypes
            ];
            options.DisplayNameClaimTypes =
            [
                ApplicationClaimTypes.Name,
                .. HttpGovernanceActorContextOptions.DefaultDisplayNameClaimTypes
            ];
        });
        services.Configure<EndpointGovernanceOptions>(options =>
        {
            options.PolicyVersion = ReferenceApplicationGovernancePolicySet.Version;
            options.PolicyHash = ReferenceApplicationGovernancePolicySet.PolicyHash;
            options.DefaultForbiddenResultFactory = httpContext => Results.Problem(
                title: "Endpoint governance denied the request.",
                detail: "The request did not satisfy the endpoint governance requirements.",
                statusCode: StatusCodes.Status403Forbidden,
                instance: httpContext.Request.Path);
        });

        services.AddScoped<IEndpointCapabilityGrantValidator, ClaimEndpointCapabilityGrantValidator>();
        services.AddScoped<IGovernanceConstraint<GovernanceEvaluationContext>, AdministratorRoleConstraint>();
        services.AddScoped<IGovernanceConstraint<GovernanceEvaluationContext>, CapabilityScopeDeclarationConstraint>();
        services.AddScoped<IGovernanceDecisionPolicy<GovernanceEvaluationContext>, ConsequentialActionDecisionPolicy>();
        services.AddScoped<IGovernancePolicyEvaluator<GovernanceEvaluationContext>>(serviceProvider =>
            DefaultGovernancePolicyEvaluator.CreateBuilder<GovernanceEvaluationContext>()
                .AddConstraints(serviceProvider.GetServices<IGovernanceConstraint<GovernanceEvaluationContext>>())
                .WithDecisionPolicy(
                    serviceProvider.GetRequiredService<IGovernanceDecisionPolicy<GovernanceEvaluationContext>>())
                .WithLogger(
                    serviceProvider.GetService<ILogger<DefaultGovernancePolicyEvaluator<GovernanceEvaluationContext>>>())
                .Build());

        return services;
    }
}
