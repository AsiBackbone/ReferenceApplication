---
title: Signed capability tokens
description: Bind a capability grant to one actor, operation, and request payload with AsiBackbone's signed capability grant validator and a host-owned issuer.
---

# Signed capability tokens

**Status:** Complete with AsiBackbone `7.3.0` ([#58](https://github.com/AsiBackbone/ReferenceApplication/issues/58)).
Disabled by default. Step 5's claim-backed grant model remains the default behavior.

Step 5 grants a capability when the actor holds a matching permission claim. That proves the actor is *entitled* to
the operation, but nothing ties the grant to a particular request. This supplement adds the second model AsiBackbone
supports: a signed capability token that authorizes exactly one request, from one actor, for one operation, within a
short window, once.

AsiBackbone `7.3.0` ships the generic, security-sensitive half of this model: the signed-grant endpoint validator, its
fail-closed handling and reason codes, the grant signing helpers, the versioned token encoding, the form-field request
binding, and the grant identifier on decision receipts. [Signed Capability Grants at Endpoints](https://asibackbone.github.io/AsiBackbone/articles/signed-capability-grants.html)
documents all of it. This page covers the decisions that remain the host's.

## Two grant models

| | Claim-backed (default) | Signed capability token |
|:---|:---|:---|
| Validator | `ClaimEndpointCapabilityGrantValidator` (host) | `SignedCapabilityGrantEndpointValidator` (AsiBackbone) |
| What proves the grant | A permission claim on the authenticated identity | A token signed by the host's issuer and sent with the request |
| Bound to | The actor, for as long as the claim exists | One actor, one operation, one request payload, one use |
| Lifetime | The session or claim lifetime | `LifetimeSeconds`, two minutes by default |
| Revocation | Removing the claim takes effect at the next sign-in or claims refresh | Tokens expire quickly; a used token cannot be replayed |
| A separate executor can check it | No: it needs the identity session | Yes: it needs only the verification key and a use store |
| Operational cost | None beyond the identity provider | A signing key to manage, and a shared use store when scaled out |

Choose claims when the actor and the executor are the same trusted process and ambient entitlement is enough. Choose
tokens when an approval should cover one specific request, when the executing component should not trust the caller's
session, or when the decision evidence must name the exact grant that authorized an action.

The models are not mixed per request. `ReferenceApplication:CapabilityTokens:Enabled` selects one for every
capability-protected endpoint when the application starts. While it is `false`, `AddApplicationCapabilityTokens` leaves
the claim validator registered. While it is `true`, it removes that registration and calls AsiBackbone's
`AddAsiBackboneSignedCapabilityGrants`, which refuses to register beside another validator. Entitlement still comes
from claims in both models: a token is issued only to an actor who holds every scope the target endpoint declares.

## What AsiBackbone provides and what the host supplies

Setting `Enabled` to `true` makes the application call AsiBackbone's registration extensions, which move the
generic, security-sensitive work into the library. The rest stays with the host, in this sample and in production.
`AddApplicationCapabilityTokens` registers everything in the same six steps as the table, and each step in the code is
marked `LIBRARY` or `HOST`.

| Step | Provided by AsiBackbone when enabled | Still required from the host | This sample | Production |
|:---|:---|:---|:---|:---|
| 1. Validator | `AddAsiBackboneSignedCapabilityGrants` registers `SignedCapabilityGrantEndpointValidator`: token reading and decoding, proof verification, subject, operation, scope, policy, validity-window and binding checks, use consumption, fail-closed denials with reason codes | Call the extension, and register no other `IEndpointCapabilityGrantValidator` | Removes the claim validator, then calls the extension | Same |
| 2. Validator options | `SignedCapabilityGrantOptions` and its startup validation | Issuer, audience, clock skew, and optional key pinning | Mapped from `ReferenceApplication:CapabilityTokens` | Same, with production values; pin the key identifier, not the version |
| 3. Request binding | `FormFieldCapabilityGrantRequestBindingProvider` (`AddAsiBackboneFormFieldCapabilityGrantBinding`) | Choose what a binding covers | Form fields, excluding antiforgery | Keep for form posts; register an `ICapabilityGrantRequestBindingProvider` for JSON bodies or route-identified resources |
| 4. Keys | `GovernanceArtifactVerifier` checks proofs through `IGovernanceSignatureVerificationService`; `AsiBackbone.Signing.ManagedKey` signs through a managed key client | The verification service, the signing service, and the key material | `CapabilityTokenKeyRing` over the configured-key `LocalDevelopmentSigningService`, PEM from configuration, one previous key for rotation | **Replace**: an `IGovernanceSignatureVerificationService` backed by the key service's public key, and `AddAsiBackboneManagedKeySigning` with an `IManagedKeySigningClient` for issuance |
| 5. Use store | `ICapabilityGrantUseStore` contract; `InMemoryCapabilityGrantUseStore` for one process | A store that fits the deployment | `InMemoryCapabilityGrantUseStore` | **Replace** when there is more than one replica: a shared store with an atomic consume |
| 6. Issuance | `GovernanceArtifactSigner.SignCapabilityGrantAsync`, `CapabilityGrantToken.Encode`, `FormFieldCapabilityGrantRequestBindingProvider.CreateBinding` | Entitlement, lifetime, use limit, and where the issuer runs | `CapabilityTokenIssuer` behind `POST /api/v1/capability-tokens`, entitlement from permission claims | Same decisions; often a separate issuing service that holds the signing key |
| Evidence | The grant identifier on the decision receipt, copied to `AuditLedgerRecord.CapabilityTokenId` and bound by the record hash | A decision receipt sink and ledger store | `LedgerDecisionReceiptSink` over the EF Core ledger, unchanged | Same |
| Time | Reads `TimeProvider` from the container | Register the clock | `TimeProvider.System`; tests replace it | Same |

Nothing in steps 1 to 3, or in the evidence path, needs host code. A host-written validator, token codec, or ledger
side channel is a sign that something the library provides is being reimplemented.

## Issue a token

`POST /api/v1/capability-tokens` requires an authenticated actor and an antiforgery token. Its form fields name the
operation and describe the payload the governed request will send:

| Field | Meaning |
|:---|:---|
| `operation` | `capability` or `consequential` |
| `payload.<field>` | One per field of the governed request; `payload.Note=approved` binds the field `Note=approved` |

The response carries the token and what it is bound to:

```json
{
  "tokenId": "3f0c1d9e6b8a4f7c9d2e1a0b5c6d7e8f",
  "token": "asibcg1.eyJ2IjoxLCJncmFudCI6...",
  "headerName": "AsiBackbone-Capability-Grant",
  "operationName": "ReferenceApplication.Web.Controllers.Api.GovernedOperationsController.ExecuteCapabilityAction (ReferenceApplication.Web)",
  "scopes": [ "referenceapplication.consequential.execute" ],
  "resourceBinding": "form-sha256:9b1c...",
  "issuedUtc": "2026-10-21T17:00:00+00:00",
  "expiresUtc": "2026-10-21T17:02:00+00:00"
}
```

The operation name is resolved from the target endpoint exactly as endpoint governance reports it, so the token and
the decision agree. Unknown operations return 400, an actor without the capability claim receives 403, and the route
returns 404 while tokens are disabled.

`CapabilityTokenIssuer` builds the grant with the actor identifier as the subject, the endpoint's operation name and
scopes, the current policy version and hash, the form-field binding from
`FormFieldCapabilityGrantRequestBindingProvider.CreateBinding`, and a use limit of one. It signs with
`GovernanceArtifactSigner.SignCapabilityGrantAsync`, which uses schema `2.0.0` so the use limit is signed, and encodes
with `CapabilityGrantToken.Encode`.

## Use the token

Send the governed request with the token in the `AsiBackbone-Capability-Grant` header and the same form fields:

```http
POST /api/v1/governed-operations/capability
AsiBackbone-Capability-Grant: asibcg1.eyJ2IjoxLCJncmFudCI6...
Content-Type: application/x-www-form-urlencoded

__RequestVerificationToken=...&Note=approved
```

The sample governed endpoints all post forms with `[ValidateAntiForgeryToken]`. The binding provider leaves the
antiforgery field out, so the per-request antiforgery token never invalidates a grant, while every other posted field
must match what was issued. A non-form body, or a form with files, cannot be bound and is denied.

## Reason codes

Every denial carries the AsiBackbone reason code for its cause, such as `endpoint.capability_grant.missing`,
`endpoint.capability_grant.proof_invalid`, `endpoint.capability_grant.resource_mismatch`,
`endpoint.capability_grant.expired`, `endpoint.capability_grant.not_yet_valid`, or
`endpoint.capability_grant.use_limit_exceeded`. The full table is in the
[AsiBackbone article](https://asibackbone.github.io/AsiBackbone/articles/signed-capability-grants.html#reason-codes).
Causes AsiBackbone's core validator would defer or escalate, such as a not-yet-valid grant, are denied at the endpoint.

As with claims, only an existing denial is preserved. A valid token keeps an acknowledgment-required decision as a
challenge, so a token never bypasses acknowledgment. The challenge consumes no use, so the acknowledged retry can
present the same single-use token.

## Evidence

When a token validates, AsiBackbone records its identifier on the decision receipt, and
`AuditLedgerRecord.FromDecisionReceipt` writes it to the ledger record's `CapabilityTokenId`. `LedgerDecisionReceiptSink`
needs no side channel for this. AsiBackbone's canonical ledger payload includes the field, so **Verify hashes** covers
it, and the evidence timeline API returns it as `capabilityTokenId` on the decision entry. Denied decisions record no
identifier, because an unverified token's identifier is caller-supplied.

## Configure

| Setting under `ReferenceApplication:CapabilityTokens` | Default | Notes |
|:---|:---|:---|
| `Enabled` | `false` | Selects the token model for every capability-protected endpoint at startup |
| `SigningKeyPem` | none | Unencrypted PEM RSA private key, at least 2048 bits. Required when enabled; never commit it |
| `KeyId`, `KeyVersion` | `referenceapplication-capability-token`, `1` | Recorded in every proof; the key identifier is also required on it |
| `PreviousVerificationKeyPem`, `PreviousKeyVersion` | none | Verification-only key during a rotation; supply both or neither. The public key is enough |
| `Issuer`, `Audience` | `referenceapplication`, `referenceapplication.governed-operations` | Written to and required on every token |
| `LifetimeSeconds` | `120` | 1 to 900 |
| `AllowedClockSkewSeconds` | `0` | 0 to 60 |
| `AllowSampleSigningKeyInProduction` | `false` | Required to enable tokens in the Production environment with the sample key provider |

Generate a key and enable tokens locally with user secrets:

```powershell
$rsa = [Security.Cryptography.RSA]::Create(2048)
dotnet user-secrets set "ReferenceApplication:CapabilityTokens:SigningKeyPem" $rsa.ExportPkcs8PrivateKeyPem() `
  --project src/ReferenceApplication.Web
dotnet user-secrets set "ReferenceApplication:CapabilityTokens:Enabled" true `
  --project src/ReferenceApplication.Web
```

Startup fails with an options validation error naming `SigningKeyPem` when tokens are enabled without a usable key.
Error messages never include the key. In the Production environment, startup also fails unless
`AllowSampleSigningKeyInProduction` is `true`, because the sample provider keeps the key in process memory. That guard
is what AsiBackbone analyzer `ASIB003` asks for, and `CapabilityTokenKeyRing` carries the
`AsiBackboneProductionConfigurationReviewed` marker that records the review.

While tokens are enabled, the governance explorer's capability and consequential actions are denied, because the
explorer does not request tokens yet. The token assembly view
([#59](https://github.com/AsiBackbone/ReferenceApplication/issues/59)) and the tampering and replay scenarios
([#62](https://github.com/AsiBackbone/ReferenceApplication/issues/62)) build on this page.

### Rotate the key

1. Set `PreviousVerificationKeyPem` and `PreviousKeyVersion` to the current key's public key and version.
2. Set a new `SigningKeyPem` and a new `KeyVersion`, then deploy. New tokens use the new key; outstanding tokens still
   verify with the previous one, because `CapabilityTokenKeyRing` routes verification by the proof's key version.
3. After one `LifetimeSeconds` plus `AllowedClockSkewSeconds`, remove the previous key and version.

Do not pin `KeyVersion` in the validator during a rotation; the Reference Application pins only the key identifier. See
the [AsiBackbone key rotation guidance](https://asibackbone.github.io/AsiBackbone/articles/signed-capability-grants.html#key-rotation).

## Wire it for production

Keep steps 1 to 3 and 6 as they are, with production values. Replace steps 4 and 5. In outline, with the host-written
types named for illustration:

```csharp
// 1. LIBRARY: the validator. Register no other IEndpointCapabilityGrantValidator.
services.AddAsiBackboneSignedCapabilityGrants(options =>
{
    // 2. HOST: what grants are validated against.
    options.Issuer = "https://approvals.example.com";
    options.Audience = "orders-api";
    options.AllowedClockSkew = TimeSpan.FromSeconds(30);
    options.ExpectedProofKeyId = "capability-grants";
});

// 3. HOST CHOICE, LIBRARY IMPLEMENTATION: what a binding covers.
services.AddAsiBackboneFormFieldCapabilityGrantBinding();

// 4. HOST: keys from a managed key service. AsiBackbone.Signing.ManagedKey signs through your key client; verification
//    is yours to supply, typically checking the proof with the key service's public key.
services.AddAsiBackboneManagedKeySigning(/* options and your IManagedKeySigningClient */);
services.AddSingleton<IGovernanceSignatureVerificationService, KeyServiceVerificationService>();

// 5. HOST: replay storage shared by every replica, with an atomic consume.
services.AddSingleton<ICapabilityGrantUseStore, DatabaseCapabilityGrantUseStore>();

// 6. HOST: issuance, here or in a separate service that holds the signing key.
services.AddSingleton<CapabilityTokenIssuer>();
```

`KeyServiceVerificationService` and `DatabaseCapabilityGrantUseStore` are not part of this sample or of AsiBackbone. A
production deployment must also supply:

- **The verification service.** `AsiBackbone.Signing.ManagedKey` provides signing only. The relying endpoint needs an
  `IGovernanceSignatureVerificationService` that checks proofs against the key service, and that trusts the previous
  key version until outstanding tokens expire, as `CapabilityTokenKeyRing` does here.
- **Key material from a secret store or key service**, never from source, with rotation as above.
- **A shared use store.** `InMemoryCapabilityGrantUseStore` is process-local: with several replicas, a single-use token
  could be accepted once by each. The shared store's consume must be atomic across replicas, backed by the database or
  a distributed cache, and must keep a consumed grant until it expires plus the allowed clock skew. See
  [multi-replica replay storage](https://asibackbone.github.io/AsiBackbone/articles/signed-capability-grants.html#multi-replica-replay-storage).
- **An issuer separate from the executor**, when that is the point of using tokens. The sample hosts both so the
  walkthrough stays self-contained. Only the issuer needs the signing key; the executor needs only the verification
  key and the use store.

Once step 4 uses a managed key service, `SigningKeyPem`, `PreviousVerificationKeyPem`, and
`AllowSampleSigningKeyInProduction` no longer apply, and the Production guard is no longer needed.

## Verification

`CapabilityTokenEndpointTests` runs the complete host against a migrated SQLite database, with an injected clock and a
signing key generated for the test run:

- an issued token authorizes its request once; the persisted decision stores the token identifier and its record hash
  verifies; a replay is denied with `use_limit_exceeded`;
- each case below is denied with its AsiBackbone reason code through the sample endpoints: no token, a tampered field,
  a different request binding, another operation, a missing scope, expired, not yet valid, and another actor;
- a token bound to its payload is accepted with the antiforgery field present;
- a valid token on the consequential endpoint still produces the acknowledgment challenge;
- issuance requires the capability claim and a known operation; the route is absent, and the claim model behaves as
  before, while tokens are disabled; and
- startup fails without a key.

`CapabilityTokenConfigurationTests` covers the host-owned configuration: key rotation through the key ring, refusal to
fall back to a generated key, option validation without echoing keys, the Production guard, and that exactly one
validator is registered for each model.
