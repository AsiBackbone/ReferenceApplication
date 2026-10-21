using System.ComponentModel.DataAnnotations;
using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Actors;
using AsiBackbone.Core.Decisions;
using Asp.Versioning;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Controllers.Api;

/// <summary>
/// Issues signed capability tokens for the capability-protected sample operations.
/// </summary>
/// <remarks>
/// <para>
/// Issuance plays the role an authorization service would play in a larger system. Entitlement still comes from the
/// actor's normalized permission claims: a token is issued only to an actor who holds every scope the target endpoint
/// declares. The token then binds that entitlement to one actor, one operation, one request payload, a short lifetime,
/// and a single use.
/// </para>
/// <para>
/// The payload is described with form fields named <c>payload.&lt;field&gt;</c>: a field <c>payload.Note</c> binds the
/// governed request's <c>Note</c> field. The binding is computed with AsiBackbone's
/// <see cref="FormFieldCapabilityGrantRequestBindingProvider.CreateBinding" />, the same rule the relying endpoint
/// applies, so every other form field the governed request posts, except the antiforgery field, invalidates the token.
/// The route is present only while capability tokens are enabled; otherwise it returns 404.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Authorize(Policy = ApplicationAuthorizationPolicyNames.AuthenticatedUser)]
[Route("api/v{version:apiVersion}/capability-tokens")]
public sealed class CapabilityTokensController(
    IOptions<CapabilityTokenOptions> optionsAccessor,
    IOptions<AntiforgeryOptions> antiforgeryOptionsAccessor,
    EndpointDataSource endpointDataSource,
    IHttpGovernanceActorContextResolver actorContextResolver,
    ClaimEndpointCapabilityGrantValidator entitlementValidator) : ControllerBase
{
    /// <summary>
    /// Gets the prefix of form fields that describe the governed request's payload.
    /// </summary>
    public const string PayloadFieldPrefix = "payload.";

    private static readonly Dictionary<string, string> _tokenOperations = new(StringComparer.Ordinal)
    {
        ["capability"] = nameof(GovernedOperationsController.ExecuteCapabilityAction),
        ["consequential"] = nameof(GovernedOperationsController.ExecuteConsequentialAction)
    };

    private readonly IOptions<CapabilityTokenOptions> _optionsAccessor =
        optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
    private readonly string _antiforgeryFieldName =
        antiforgeryOptionsAccessor?.Value.FormFieldName
        ?? throw new ArgumentNullException(nameof(antiforgeryOptionsAccessor));
    private readonly EndpointDataSource _endpointDataSource =
        endpointDataSource ?? throw new ArgumentNullException(nameof(endpointDataSource));
    private readonly IHttpGovernanceActorContextResolver _actorContextResolver =
        actorContextResolver ?? throw new ArgumentNullException(nameof(actorContextResolver));
    private readonly ClaimEndpointCapabilityGrantValidator _entitlementValidator =
        entitlementValidator ?? throw new ArgumentNullException(nameof(entitlementValidator));

    /// <summary>
    /// Issues a single-use capability token for one governed request.
    /// </summary>
    /// <param name="operation">The governed operation: <c>capability</c> or <c>consequential</c>.</param>
    /// <param name="cancellationToken">A token that cancels issuance.</param>
    /// <returns>The token and the values it is bound to.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [ProducesResponseType<CapabilityTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CapabilityTokenResponse>> Issue(
        [FromForm, Required, StringLength(32, MinimumLength = 1)] string operation,
        CancellationToken cancellationToken)
    {
        if (!_optionsAccessor.Value.Enabled)
        {
            return NotFound();
        }

        if (!_tokenOperations.TryGetValue(operation.Trim(), out string? actionName))
        {
            return Problem(
                title: "Unknown governed operation.",
                detail: "Capability tokens can be issued for the 'capability' and 'consequential' operations.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        Endpoint endpoint = FindGovernedEndpoint(actionName)
            ?? throw new InvalidOperationException($"The governed operation endpoint '{actionName}' is not mapped.");
        var descriptor = EndpointGovernanceDescriptor.FromEndpoint(endpoint);

        IGovernanceActorContext actor = _actorContextResolver.ResolveActorContext();
        GovernanceDecision entitlement = await _entitlementValidator
            .ValidateAsync(HttpContext, descriptor, GovernanceDecision.Allow(), cancellationToken)
            .ConfigureAwait(false);
        if (!actor.IsAuthenticated || entitlement.IsDenied)
        {
            return Problem(
                title: "Capability token not issued.",
                detail: "The actor does not hold every capability the operation requires.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        IFormCollection form = await Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        KeyValuePair<string, StringValues>[] payloadFields = [.. form
            .Where(static field => field.Key.StartsWith(PayloadFieldPrefix, StringComparison.Ordinal))
            .Select(static field => new KeyValuePair<string, StringValues>(
                field.Key[PayloadFieldPrefix.Length..],
                field.Value))
            .Where(field => !string.Equals(field.Key, _antiforgeryFieldName, StringComparison.OrdinalIgnoreCase))];

        // Resolved only once tokens are known to be enabled, because the key ring requires a configured key.
        CapabilityTokenIssuer issuer = HttpContext.RequestServices.GetRequiredService<CapabilityTokenIssuer>();
        var issueRequest = new CapabilityTokenIssueRequest(
            actor.ActorId,
            descriptor.OperationName,
            descriptor.CapabilityScopes,
            payloadFields);
        IssuedCapabilityToken issued = await issuer
            .IssueAsync(issueRequest, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new CapabilityTokenResponse(
            TokenId: issued.TokenId,
            Token: issued.Token,
            HeaderName: SignedCapabilityGrantOptions.DefaultTokenHeaderName,
            OperationName: issued.OperationName,
            Scopes: issued.Scopes,
            ResourceBinding: issued.ResourceBinding,
            IssuedUtc: issued.IssuedUtc,
            ExpiresUtc: issued.ExpiresUtc));
    }

    private Endpoint? FindGovernedEndpoint(string actionName)
    {
        return _endpointDataSource.Endpoints.FirstOrDefault(endpoint =>
            endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action &&
            action.ControllerTypeInfo.AsType() == typeof(GovernedOperationsController) &&
            string.Equals(action.ActionName, actionName, StringComparison.Ordinal));
    }
}

/// <summary>
/// An issued capability token and the values it is bound to.
/// </summary>
/// <param name="TokenId">The token identifier recorded in decision evidence when the token is used.</param>
/// <param name="Token">The encoded token.</param>
/// <param name="HeaderName">The request header that must carry the token.</param>
/// <param name="OperationName">The governed operation the token is bound to.</param>
/// <param name="Scopes">The granted capability scopes.</param>
/// <param name="ResourceBinding">The request payload binding.</param>
/// <param name="IssuedUtc">When the token was issued, and when it becomes valid.</param>
/// <param name="ExpiresUtc">When the token expires.</param>
public sealed record CapabilityTokenResponse(
    string TokenId,
    string Token,
    string HeaderName,
    string OperationName,
    IReadOnlyList<string> Scopes,
    string ResourceBinding,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc);
