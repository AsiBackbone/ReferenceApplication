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
/// <para>
/// Issuance is host-owned: AsiBackbone does not issue grants. The library supplies the signing helper, the token
/// encoding, and <see cref="FormFieldCapabilityGrantRequestBindingProvider.CreateBinding" />; the host decides who is
/// entitled, the lifetime, the use limit, which key signs, and where the issuer runs. The signature must verify under
/// a public key the relying endpoint's <see cref="IGovernanceSignatureVerificationService" /> trusts; in this sample
/// that is a <see cref="CapabilityTokenOptions.VerificationKeys" /> entry, and in production it is the public key of
/// the managed key service that signs.
/// </para>
/// </remarks>
public sealed class CapabilityTokenIssuer(
    IOptions<CapabilityTokenOptions> optionsAccessor,
    CapabilityTokenSigningKey signingKey,
    TimeProvider timeProvider)
{
    private readonly IOptions<CapabilityTokenOptions> _optionsAccessor =
        optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
    private readonly CapabilityTokenSigningKey _signingKey =
        signingKey ?? throw new ArgumentNullException(nameof(signingKey));
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
        CapabilityGrant grant = CreateGrant(request);

        SignedGovernanceArtifact<CapabilityGrant> signedGrant = await GovernanceArtifactSigner
            .SignCapabilityGrantAsync(
                grant,
                _signingKey.Signer,
                keyId: _signingKey.KeyId,
                keyVersion: _signingKey.KeyVersion,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new IssuedCapabilityToken(
            TokenId: signedGrant.Artifact.TokenId,
            Token: CapabilityGrantToken.Encode(signedGrant),
            OperationName: request.OperationName,
            Scopes: signedGrant.Artifact.Scopes,
            ResourceBinding: signedGrant.Artifact.ResourceBinding ?? string.Empty,
            IssuedUtc: signedGrant.Artifact.IssuedUtc,
            ExpiresUtc: signedGrant.Artifact.ExpiresUtc);
    }

    /// <summary>
    /// Builds the unsigned grant that <see cref="IssueAsync" /> would sign for a request.
    /// </summary>
    /// <param name="request">What the token is bound to.</param>
    /// <returns>The grant, with a new token identifier and the current issuance time.</returns>
    /// <remarks>
    /// The Development policy simulator previews a token through this method, so the preview shows exactly the claims,
    /// binding, and validity window issuance would sign, without signing anything or consuming a use.
    /// </remarks>
    public CapabilityGrant CreateGrant(CapabilityTokenIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationName);
        ArgumentNullException.ThrowIfNull(request.Scopes);
        ArgumentNullException.ThrowIfNull(request.PayloadFields);

        CapabilityTokenOptions options = _optionsAccessor.Value;
        DateTimeOffset issuedUtc = TruncateToSeconds(_timeProvider.GetUtcNow());
        string resourceBinding = FormFieldCapabilityGrantRequestBindingProvider.CreateBinding(request.PayloadFields);

        return CapabilityGrant.Create(
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
