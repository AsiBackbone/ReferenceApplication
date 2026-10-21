using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.Core.CapabilityGrants;
using AsiBackbone.Core.Signing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Issues signed, short-lived, single-use capability tokens bound to one actor, operation, and request payload.
/// </summary>
/// <remarks>
/// <para>
/// Every token is an AsiBackbone <see cref="CapabilityGrant" /> that carries the capability scopes, the subject (the
/// governance actor identifier), the governed operation name, issue, not-before and expiry times, the governance
/// policy version and hash, a form-field request binding, and a use limit of one. Signing and encoding use
/// AsiBackbone's <see cref="GovernanceArtifactSigner.SignCapabilityGrantAsync" /> and
/// <see cref="CapabilityGrantToken" />, so the use limit is covered by the signature and the relying endpoint decodes
/// the token with the same library code.
/// </para>
/// <para>
/// Issuance does not decide entitlement. The caller checks that the actor may hold the requested scopes before
/// calling <see cref="IssueAsync" />.
/// </para>
/// </remarks>
public sealed class CapabilityTokenIssuer(
    IOptions<CapabilityTokenOptions> optionsAccessor,
    CapabilityTokenKeyRing keyRing,
    TimeProvider timeProvider)
{
    private readonly IOptions<CapabilityTokenOptions> _optionsAccessor =
        optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
    private readonly CapabilityTokenKeyRing _keyRing =
        keyRing ?? throw new ArgumentNullException(nameof(keyRing));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// Issues a signed capability token.
    /// </summary>
    /// <param name="request">What the token is bound to.</param>
    /// <param name="cancellationToken">A token that cancels signing.</param>
    /// <returns>The encoded token and the values it is bound to.</returns>
    public async ValueTask<IssuedCapabilityToken> IssueAsync(
        CapabilityTokenIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationName);
        ArgumentNullException.ThrowIfNull(request.Scopes);
        ArgumentNullException.ThrowIfNull(request.PayloadFields);

        CapabilityTokenOptions options = _optionsAccessor.Value;
        DateTimeOffset issuedUtc = TruncateToSeconds(_timeProvider.GetUtcNow());
        string resourceBinding = FormFieldCapabilityGrantRequestBindingProvider.CreateBinding(request.PayloadFields);

        var grant = CapabilityGrant.Create(
            tokenId: Guid.NewGuid().ToString("N"),
            issuer: options.Issuer,
            audience: options.Audience,
            scopes: request.Scopes,
            issuedUtc: issuedUtc,
            expiresUtc: issuedUtc.AddSeconds(options.LifetimeSeconds),
            notBeforeUtc: issuedUtc,
            subjectId: request.SubjectId,
            operationName: request.OperationName,
            policyVersion: ReferenceApplicationGovernancePolicySet.Version,
            policyHash: ReferenceApplicationGovernancePolicySet.PolicyHash,
            resourceBinding: resourceBinding,
            maxUseCount: 1);

        SignedGovernanceArtifact<CapabilityGrant> signedGrant = await GovernanceArtifactSigner
            .SignCapabilityGrantAsync(
                grant,
                _keyRing.Signer,
                keyId: _keyRing.KeyId,
                keyVersion: _keyRing.KeyVersion,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new IssuedCapabilityToken(
            TokenId: signedGrant.Artifact.TokenId,
            Token: CapabilityGrantToken.Encode(signedGrant),
            OperationName: request.OperationName,
            Scopes: signedGrant.Artifact.Scopes,
            ResourceBinding: resourceBinding,
            IssuedUtc: signedGrant.Artifact.IssuedUtc,
            ExpiresUtc: signedGrant.Artifact.ExpiresUtc);
    }

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Offset);
    }
}

/// <summary>
/// Describes what a capability token is bound to.
/// </summary>
/// <param name="SubjectId">The governance actor identifier the token is issued to.</param>
/// <param name="OperationName">The governed operation name, as endpoint governance reports it.</param>
/// <param name="Scopes">The capability scopes the token grants.</param>
/// <param name="PayloadFields">
/// The form fields the governed request will post, excluding the antiforgery and token fields, which the relying
/// endpoint leaves out of the binding.
/// </param>
public sealed record CapabilityTokenIssueRequest(
    string SubjectId,
    string OperationName,
    IReadOnlyList<string> Scopes,
    IReadOnlyCollection<KeyValuePair<string, StringValues>> PayloadFields);

/// <summary>
/// An issued capability token and the values it is bound to.
/// </summary>
/// <param name="TokenId">The unique token identifier recorded in decision evidence when the token is used.</param>
/// <param name="Token">The encoded token to send in the capability grant request header.</param>
/// <param name="OperationName">The governed operation the token is bound to.</param>
/// <param name="Scopes">The granted capability scopes.</param>
/// <param name="ResourceBinding">The form-field binding the token is bound to.</param>
/// <param name="IssuedUtc">When the token was issued, and when it becomes valid.</param>
/// <param name="ExpiresUtc">When the token expires.</param>
public sealed record IssuedCapabilityToken(
    string TokenId,
    string Token,
    string OperationName,
    IReadOnlyList<string> Scopes,
    string ResourceBinding,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc);
