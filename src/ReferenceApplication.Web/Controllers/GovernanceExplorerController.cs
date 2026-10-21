using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Authentication.Demo;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Extensions;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Governance.Demonstration;
using ReferenceApplication.Web.Governance.Simulation;
using ReferenceApplication.Web.Models;

namespace ReferenceApplication.Web.Controllers;

/// <summary>
/// Presents the interactive governance evidence explorer.
/// </summary>
[Authorize(Policy = ApplicationAuthorizationPolicyNames.AuthenticatedUser)]
public sealed class GovernanceExplorerController(
    IAuthorizationService authorizationService,
    IOptions<ApplicationAuthorizationOptions> authorizationOptions,
    IConfiguration configuration,
    IHostEnvironment environment,
    IEnumerable<IDemoIdentityService> demoIdentityServices,
    IEnumerable<IGovernanceLoadDemonstrationService> loadDemonstrationServices,
    IEnumerable<IPolicySimulator> policySimulators) : Controller
{
    private readonly IAuthorizationService _authorizationService =
        authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
    private readonly ApplicationAuthorizationOptions _authorizationOptions =
        authorizationOptions?.Value ?? throw new ArgumentNullException(nameof(authorizationOptions));
    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly IHostEnvironment _environment =
        environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly bool _hasDemoIdentityService =
        demoIdentityServices?.Any() == true;
    private readonly bool _hasLoadDemonstrationService =
        loadDemonstrationServices?.Any() == true;
    private readonly IPolicySimulator? _policySimulator =
        policySimulators?.FirstOrDefault();

    /// <summary>
    /// Displays the explorer for an authenticated actor.
    /// </summary>
    /// <returns>The explorer view.</returns>
    [HttpGet("/governance-explorer")]
    public async Task<IActionResult> Index()
    {
        AuthorizationResult administrator = await _authorizationService
            .AuthorizeAsync(User, ApplicationAuthorizationPolicyNames.AdministratorRole)
            .ConfigureAwait(false);

        bool hasCapability = User.HasClaim(
            _authorizationOptions.PermissionClaimType,
            ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope);
        string actorDisplayName = User.FindFirst(ApplicationClaimTypes.Name)?.Value
            ?? User.Identity?.Name
            ?? "Authenticated actor";
        GovernanceLoadDemonstrationOptions loadOptions = _configuration
            .GetSection(GovernanceLoadDemonstrationOptions.SectionName)
            .Get<GovernanceLoadDemonstrationOptions>() ?? new GovernanceLoadDemonstrationOptions();
        bool showPolicySimulator = administrator.Succeeded &&
            _policySimulator is not null &&
            PolicySimulatorServiceExtensions.IsActive(_configuration, _environment);

        return View(new GovernanceExplorerViewModel
        {
            ActorDisplayName = actorDisplayName,
            CanViewEvidence = administrator.Succeeded,
            HasAdministratorRole = administrator.Succeeded,
            HasConsequentialCapability = hasCapability,
            DemonstrationDatabaseWasReset = string.Equals(
                Request.Query["databaseReset"].ToString(),
                bool.TrueString,
                StringComparison.OrdinalIgnoreCase),
            ShowDemoIdentityControls = _hasDemoIdentityService &&
                DemoIdentityServiceExtensions.IsDemoIdentityActive(_configuration, _environment),
            ShowGovernanceLoadDemonstration = administrator.Succeeded &&
                _hasLoadDemonstrationService &&
                GovernanceLoadDemonstrationServiceExtensions.IsActive(_configuration, _environment),
            LoadDemonstrationDefaultIterations = loadOptions.DefaultIterations,
            LoadDemonstrationMaximumIterations = loadOptions.MaximumIterations,
            LoadDemonstrationDefaultDurationMilliseconds = loadOptions.DefaultDurationMilliseconds,
            LoadDemonstrationMaximumDurationMilliseconds = loadOptions.MaximumDurationMilliseconds,
            ShowPolicySimulator = showPolicySimulator,
            PolicySimulatorConstraintNames = showPolicySimulator ? _policySimulator!.ConstraintNames : [],
            CapabilityTokensEnabled = _configuration.GetValue<bool>(
                $"{CapabilityTokenOptions.SectionName}:{nameof(CapabilityTokenOptions.Enabled)}")
        });
    }
}
