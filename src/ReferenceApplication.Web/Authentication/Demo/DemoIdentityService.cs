using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Authentication.Demo;

/// <summary>
/// Creates the fixed demonstration identity used to exercise walkthrough governance outcomes.
/// </summary>
public interface IDemoIdentityService
{
    /// <summary>
    /// Creates an authenticated demo principal with only the selected fixed claims.
    /// </summary>
    /// <param name="includeAdministratorRole">Whether to add the configured administrator role.</param>
    /// <param name="includeConsequentialCapability">Whether to add the walkthrough capability grant.</param>
    /// <returns>The demo principal.</returns>
    ClaimsPrincipal CreatePrincipal(
        bool includeAdministratorRole,
        bool includeConsequentialCapability);
}

/// <summary>
/// Creates a local demonstration principal without accepting arbitrary claim names or values.
/// </summary>
internal sealed class DemoIdentityService(
    IOptions<ApplicationAuthorizationOptions> authorizationOptionsAccessor) : IDemoIdentityService
{
    /// <summary>
    /// The stable subject assigned to the demo identity.
    /// </summary>
    internal const string ActorId = "referenceapplication-demo-actor";

    private readonly IOptions<ApplicationAuthorizationOptions> _authorizationOptionsAccessor =
        authorizationOptionsAccessor ?? throw new ArgumentNullException(nameof(authorizationOptionsAccessor));

    /// <inheritdoc />
    public ClaimsPrincipal CreatePrincipal(
        bool includeAdministratorRole,
        bool includeConsequentialCapability)
    {
        ApplicationAuthorizationOptions options = _authorizationOptionsAccessor.Value;
        List<Claim> claims =
        [
            new(ApplicationClaimTypes.Subject, ActorId),
            new(ApplicationClaimTypes.Name, "Governance demo actor")
        ];

        if (includeAdministratorRole)
        {
            claims.Add(new Claim(
                options.RoleClaimType,
                options.AdministratorRoles.First(role => !string.IsNullOrWhiteSpace(role))));
        }

        if (includeConsequentialCapability)
        {
            claims.Add(new Claim(
                options.PermissionClaimType,
                ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope));
        }

        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme,
            ApplicationClaimTypes.Name,
            options.RoleClaimType);
        return new ClaimsPrincipal(identity);
    }
}
