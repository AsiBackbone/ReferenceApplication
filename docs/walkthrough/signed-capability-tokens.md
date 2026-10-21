---
title: Signed capability tokens
description: Bind a capability grant to one actor, operation, and request payload with AsiBackbone's signed capability grant validator and a host-owned issuer.
---

# Signed capability tokens

**Status:** Complete with AsiBackbone `7.4.0` ([#58](https://github.com/AsiBackbone/ReferenceApplication/issues/58)).
Disabled by default. Step 5's claim-backed grant model remains the default behavior.

Step 5 grants a capability when the actor holds a matching permission claim. That proves the actor is *entitled* to
the operation, but nothing ties the grant to a particular request. This supplement adds the second model AsiBackbone
supports: a signed capability token that authorizes exactly one request, from one actor, for one operation, within a
short window, once.

AsiBackbone ships the generic, security-sensitive half of this model. `7.3.0` added the signed-grant endpoint
validator, its fail-closed handling and reason codes, the grant signing helpers, the versioned token encoding, the
form-field request binding, and the grant identifier on decision receipts. `7.4.0` added the production pieces the host
previously wrote itself: a public-key signature verifier with key-version rotation and revocation, and a durable EF Core
use store whose consume is atomic across replicas. [Signed Capability Grants at Endpoints](https://asibackbone.github.io/AsiBackbone/articles/signed-capability-grants.html)
documents all of it. This page covers the decisions that remain the host's.

## Two grant models

| | Claim-backed (default) | Signed capability token |
|:---|:---|:---|
| Validator | `ClaimEndpointCapabilityGrantValidator` (host) | `SignedCapabilityGrantEndpointValidator` (AsiBackbone) |
| What proves the grant | A permission claim on the authenticated identity | A token signed by the host's issuer and sent with the request |
| Bound to | The actor, for as long as the claim exists | One actor, one operation, one request payload, one use |
| Lifetime | The session or claim lifetime | `LifetimeSeconds`, two minutes by default |
| Revocation | Removing the claim takes effect at the next sign-in or claims refresh | Tokens expire quickly; a used token cannot be replayed |
| A separate executor can check it | No: it needs the identity session | Yes: it needs only the public verification keys and the use store |
| Operational cost | None beyond the identity provider | A signing key to manage and its public keys to distribute, and a use-store table to migrate and purge |

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
| 4. Verification keys | `AddAsiBackboneKeySignatureVerification` registers `PublicKeySignatureVerificationService`: selects the trusted key by the proof's key identifier and version, keeps previous versions verifying during a rotation, denies revoked versions, and refuses private or undersized key material | The trusted public keys, from configuration or a key resolver | `VerificationKeys` from configuration, PEM public keys only | Same, with the managed key service's public keys; or `IGovernanceVerificationKeyResolver` for a JWKS endpoint or key service |
| 5. Use store | `AddAsiBackboneEfCoreCapabilityGrantUseStore<TDbContext>` registers `EfCoreCapabilityGrantUseStore`: a unique issuer and token key with a conditional increment, so replicas sharing the database accept a single-use grant once, and retention until expiry plus a grace period | The host-owned `DbContext` and its migration, the retention grace period, and a scheduled purge | `ApplicationDbContext`, migration `AddCapabilityGrantUses`, a 60-second grace period, `CapabilityGrantUsePurgeService` | Same |
| 6. Issuance | `GovernanceArtifactSigner.SignCapabilityGrantAsync`, `CapabilityGrantToken.Encode`, `FormFieldCapabilityGrantRequestBindingProvider.CreateBinding`; `AsiBackbone.Signing.ManagedKey` signs through a managed key client | Entitlement, lifetime, use limit, the signing key, and where the issuer runs | `CapabilityTokenIssuer` behind `POST /api/v1/capability-tokens`, entitlement from permission claims, signed by `CapabilityTokenSigningKey` with a PEM private key from configuration | **Replace the signing key**: `AddAsiBackboneManagedKeySigning` with an `IManagedKeySigningClient`, often in a separate issuing service |
| Evidence | The grant identifier on the decision receipt, copied to `AuditLedgerRecord.CapabilityTokenId` and bound by the record hash | A decision receipt sink and ledger store | `LedgerDecisionReceiptSink` over the EF Core ledger, unchanged | Same |
| Time | Reads `TimeProvider` from the container | Register the clock | `TimeProvider.System`; tests replace it | Same |

Nothing in steps 1 to 5, or in the evidence path, needs host code beyond configuration, the migration, and the purge
schedule. A host-written validator, token codec, signature verifier, use store, or ledger side channel is a sign that
something the library provides is being reimplemented.

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
| `SigningKeyPem` | none | Unencrypted PEM RSA private key, at least 2048 bits, used only at issuance. Required when enabled; never commit it |
| `KeyId`, `KeyVersion` | `referenceapplication-capability-token`, `1` | Recorded in every proof; the key identifier is also required on it, and `KeyVersion` names the signing key |
| `VerificationKeys:<n>:KeyVersion`, `VerificationKeys:<n>:PublicKeyPem`, `VerificationKeys:<n>:Revoked` | none | Trusted public keys under `KeyId`. Required when enabled, and must include the public key of `SigningKeyPem` under `KeyVersion` |
| `Issuer`, `Audience` | `referenceapplication`, `referenceapplication.governed-operations` | Written to and required on every token |
| `LifetimeSeconds` | `120` | 1 to 900 |
| `AllowedClockSkewSeconds` | `0` | 0 to 60 |
| `UseStorePurgeIntervalSeconds` | `300` | 10 to 86,400; how often the use store is purged |
| `AllowSampleSigningKeyInProduction` | `false` | Required to enable tokens in the Production environment with the sample issuance signing key |

Generate a key, trust its public half, and enable tokens locally with user secrets:

```powershell
$rsa = [Security.Cryptography.RSA]::Create(2048)
dotnet user-secrets set "ReferenceApplication:CapabilityTokens:SigningKeyPem" $rsa.ExportPkcs8PrivateKeyPem() `
  --project src/ReferenceApplication.Web
dotnet user-secrets set "ReferenceApplication:CapabilityTokens:VerificationKeys:0:KeyVersion" 1 `
  --project src/ReferenceApplication.Web
dotnet user-secrets set "ReferenceApplication:CapabilityTokens:VerificationKeys:0:PublicKeyPem" `
  $rsa.ExportSubjectPublicKeyInfoPem() --project src/ReferenceApplication.Web
dotnet user-secrets set "ReferenceApplication:CapabilityTokens:Enabled" true `
  --project src/ReferenceApplication.Web
```

Apply the migrations before enabling tokens, as in [Local database setup](local-database-setup.md). Migration
`AddCapabilityGrantUses` creates `AsiBackboneCapabilityGrantUses`, the table the use store writes to. On a database that
predates it, every token is denied as `endpoint.capability_grant.use_store_unavailable`, because a store that cannot
answer fails closed.

Startup fails with an options validation error when tokens are enabled without a usable `SigningKeyPem`, without
`VerificationKeys`, or when no unrevoked `VerificationKeys` entry under `KeyVersion` matches the signing key's public
key, so the application never issues tokens it cannot verify. Error messages name the setting and never include a
key. In the Production environment, startup also fails unless `AllowSampleSigningKeyInProduction` is `true`, because
the sample issuance signer keeps the private key in process memory. That guard is what AsiBackbone analyzer `ASIB003`
asks for, and `CapabilityTokenSigningKey` carries the `AsiBackboneProductionConfigurationReviewed` marker that records
the review. Verification and replay storage use AsiBackbone's production components and are not behind the guard.

While tokens are enabled, the governance explorer's capability and consequential actions are denied, because the
explorer does not request tokens yet. The explorer's
[policy simulator](07-observe-governance.md#policy-simulator)
([#59](https://github.com/AsiBackbone/ReferenceApplication/issues/59)) shows the token issuance would sign for a
simulated request, without signing it or consuming a use. [Decision replay](07-observe-governance.md#decision-replay)
([#60](https://github.com/AsiBackbone/ReferenceApplication/issues/60)) never verifies a token again: the ledger stores
only the accepted grant's identifier, so a replay uses the recorded grant outcome. The tampering and replay scenarios
([#62](https://github.com/AsiBackbone/ReferenceApplication/issues/62)) build on this page.

### Rotate the key

1. Add the new key's public half to `VerificationKeys` under a new version, and deploy. Every instance now trusts both
   versions.
2. Set the new `SigningKeyPem` and `KeyVersion`, and deploy. New tokens use the new key; outstanding tokens still
   verify, because AsiBackbone's verifier selects the key by the proof's version.
3. After one `LifetimeSeconds` plus `AllowedClockSkewSeconds`, remove the previous version from `VerificationKeys`.

To cut off a compromised key at once, set its entry's `Revoked` to `true` instead of removing it: the verifier then
fails proofs signed under it as `signature.key-revoked`, and the endpoint denies them as
`endpoint.capability_grant.proof_invalid`. Do not pin `KeyVersion` in the validator during a rotation; the Reference
Application pins only the key identifier. See the
[AsiBackbone key rotation guidance](https://asibackbone.github.io/AsiBackbone/articles/signed-capability-grants.html#key-rotation).

### Replay storage

Every instance sharing `ApplicationDatabase` consumes uses through the same table, so a single-use token is accepted
exactly once however many instances run. The store keeps each record until the grant expires plus 60 seconds, the
largest clock skew the configuration accepts, so a purge can never let a replay through.
`CapabilityGrantUsePurgeService` calls `PurgeExpiredAsync` every `UseStorePurgeIntervalSeconds`, starting one interval
after startup; several instances purging at once is harmless. See
[multi-replica replay storage](https://asibackbone.github.io/AsiBackbone/articles/signed-capability-grants.html#multi-replica-replay-storage).

## Wire it for production

Keep steps 1 to 5 as they are, with production values and keys. Replace the issuance signing key in step 6. In outline,
with the host-written client named for illustration:

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

// 4. LIBRARY VERIFIER, HOST KEYS: the key service's public keys, current and previous.
services.AddAsiBackboneKeySignatureVerification(options =>
{
    options.AddPemKey("capability-grants", "2026-10", ManagedKeySignatureAlgorithms.RsaPssSha256, currentPublicPem);
    options.AddPemKey("capability-grants", "2026-09", ManagedKeySignatureAlgorithms.RsaPssSha256, previousPublicPem);
});

// 5. LIBRARY STORE, HOST DATABASE: replay storage shared by every replica, plus its migration and purge schedule.
services.AddAsiBackboneEfCoreCapabilityGrantUseStore<ApplicationDbContext>(options =>
    options.RetentionGracePeriod = TimeSpan.FromSeconds(30));
services.AddHostedService<CapabilityGrantUsePurgeService>();

// 6. HOST: issuance, signed by the managed key service, here or in a separate issuing service.
services.AddAsiBackboneManagedKeySigning(/* options and your IManagedKeySigningClient */);
services.AddSingleton<CapabilityTokenIssuer>();
```

Only step 6 still needs a production decision of its own. A production deployment must also supply:

- **A managed signing key.** `AddAsiBackboneManagedKeySigning` signs through a host-owned `IManagedKeySigningClient`
  for the key service, so the private key never enters the process. The issuer then signs with the registered
  `IGovernanceSigningService` instead of `CapabilityTokenSigningKey`.
- **The public keys of that key service** in step 4, with rotation as above. Use `IGovernanceVerificationKeyResolver`
  when they come from a JWKS endpoint or the key service rather than configuration.
- **The use-store migration** applied with the application's other migrations, a retention grace period of at least the
  validator's clock skew, and a scheduled purge.
- **An issuer separate from the executor**, when that is the point of using tokens. The sample hosts both so the
  walkthrough stays self-contained. Only the issuer needs the signing key; the executor needs only the public keys and
  the use store.

Once issuance uses a managed key service, `SigningKeyPem` and `AllowSampleSigningKeyInProduction` no longer apply, and
the Production guard is no longer needed.

## Verification

`CapabilityTokenEndpointTests` runs the complete host against a migrated SQLite database, with an injected clock, a
signing key generated for the test run, and verification configured with its public key only:

- an issued token authorizes its request once; the persisted decision stores the token identifier and its record hash
  verifies; a replay is denied with `use_limit_exceeded`;
- two application instances sharing the database each verify the token, and together accept it exactly once;
- each case below is denied with its AsiBackbone reason code through the sample endpoints: no token, a tampered field,
  a different request binding, another operation, a missing scope, expired, not yet valid, and another actor;
- a token bound to its payload is accepted with the antiforgery field present;
- a valid token on the consequential endpoint still produces the acknowledgment challenge;
- the purge keeps a consumed grant through its retention horizon and removes it afterwards;
- issuance requires the capability claim and a known operation; the route is absent, and the claim model behaves as
  before, while tokens are disabled; and
- startup fails without a key.

`CapabilityTokenConfigurationTests` covers the host-owned configuration: rotation and revocation through AsiBackbone's
public-key verifier configured from `VerificationKeys`, refusal to fall back to a generated signing key, option
validation without echoing keys, the requirement that the signing key be trusted under its own version, the Production
guard, that exactly one validator is registered for each model, and that verification and replay storage are registered
only through AsiBackbone's extensions.
