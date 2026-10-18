using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.DependencyInjection;
using ReferenceApplication.Web.Authentication.Claims;

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

        return services;
    }
}
