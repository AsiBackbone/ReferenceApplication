---
title: "Step 5: Govern Endpoints"
description: The production sample endpoints, claim-backed capability boundary, HTTP outcomes, and end-to-end verification.
---

# Step 5: Govern Endpoints

**Status:** Complete with AsiBackbone `7.2.1`. First released in [`v0.1.0-alpha.5`](https://github.com/AsiBackbone/ReferenceApplication/releases/tag/v0.1.0-alpha.5) on AsiBackbone `7.2.0`; see [Capability before acknowledgment](#capability-before-acknowledgment) for why the step now requires `7.2.1`.

## Outcome

The versioned `GovernedOperationsController` demonstrates governance on real application endpoints before their action
bodies execute. It keeps a read-only description route intentionally ungoverned and places three unsafe operations
behind explicit host-owned rules:

| Endpoint | Governance metadata | Successful outcome |
|:---|:---|:---|
| `GET /api/v1/governed-operations` | None; explicitly anonymous | Returns the read-only sample description. |
| `POST /api/v1/governed-operations/administrator` | `AdministratorActionPolicy` | Executes for an authenticated actor in a configured administrator role. |
| `POST /api/v1/governed-operations/capability` | `CapabilityProtectedActionPolicy` and `referenceapplication.consequential.execute` capability | Executes when the actor holds the declared capability. |
| `POST /api/v1/governed-operations/consequential` | All three policy markers, the capability scope, and acknowledgment metadata | Returns an acknowledgment challenge to an administrator who holds the capability; an administrator without it is denied. The action body does not execute. |

The POST routes keep NCAT's authenticated-by-default authorization policy and validate antiforgery tokens explicitly
with `[ValidateAntiForgeryToken]`, matching NCAT's own `AccountController`. Endpoint governance adds an execution
boundary; it does not replace either control.

For local exploration without an external identity provider, the supplemental
[Development-only demo identity controls](development-demo-identity.md) can issue a temporary cookie containing fixed,
selectable walkthrough claims. The control is off by default and is not registered outside `Development`.

## Attach endpoint metadata

Controller attributes make the protected operations and the order-independent requirements visible next to the action:

```csharp
[HttpPost("consequential")]
[ValidateAntiForgeryToken]
[GovernancePolicy(typeof(AdministratorActionPolicy))]
[GovernancePolicy(typeof(CapabilityProtectedActionPolicy))]
[GovernancePolicy(typeof(ConsequentialActionPolicy))]
[RequireCapabilityGrant(ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope)]
[RequireAcknowledgment]
public ActionResult<GovernedOperationResponse> ExecuteConsequentialAction()
```

Routing selects that endpoint, NCAT authenticates and authorizes the request, and AsiBackbone evaluates its metadata
before MVC invokes the action. A denied decision or an acknowledgment challenge therefore prevents the response text
inside the action from being produced.

`GET /api/v1/governed-operations` is a read-only description. It is marked `AllowAnonymous` and has no AsiBackbone
metadata, so the middleware passes it through. This is deliberate rather than an omission: reading the sample does not
perform the operation being governed.

## Validate capability grants

`ClaimEndpointCapabilityGrantValidator` is the host-owned grant boundary. AsiBackbone supplies the endpoint's declared
scopes but does not infer where grants come from. The validator reads the current actor's authenticated identities and
requires every declared scope to appear as an exact claim using
`ApplicationAuthorizationOptions.PermissionClaimType`.

With the shipped NCAT configuration, authentication-provider claims such as `permission`, `permissions`, `scope`, or
`scp` are normalized to `application:permission`. Providers should issue one claim value per capability. A scope in
endpoint metadata only states what the operation requires; it never proves the actor holds that grant.

The validator preserves only a decision that is already denied. Every other decision, including one that requires
acknowledgment, must pass the grant check, and a missing grant is denied with
`referenceapplication.capability.grant_required`. AsiBackbone `7.2.1` calls the validator for every decision that is not
already denied, so a missing grant always takes precedence over an acknowledgment challenge. Registration is scoped to
the request:

```csharp
services.AddScoped<IEndpointCapabilityGrantValidator, ClaimEndpointCapabilityGrantValidator>();
```

A claim proves entitlement but does not tie a grant to any particular request. The supplemental
[Bind capabilities to signed tokens](signed-capability-tokens.md) procedure adopts AsiBackbone's second model, signed
capability tokens bound to one actor, operation, and payload, and compares the two.

## Map HTTP outcomes

The endpoint middleware now uses the application's Problem Details service for ordinary governance denials. The body is
intentionally generic: it reports HTTP 403 without exposing policy internals, while NCAT adds its normal request and
trace correlation fields.

An otherwise-allowed consequential request produces AsiBackbone's acknowledgment challenge with HTTP 428
`Precondition Required`. The challenge includes the handshake identifier and stable acknowledgment reason code. Step 5
demonstrates that execution is withheld at this boundary; storing and completing a host-owned acknowledgment workflow
is outside this sample step.

The effective order is:

1. authentication and ASP.NET Core authorization;
2. AsiBackbone policy evaluation;
3. host-owned capability validation, for every decision that is not already denied;
4. denial or acknowledgment challenge when required;
5. MVC antiforgery validation when governance allows the request to continue; and
6. MVC action execution only when every boundary permits it.

Antiforgery validation still applies to every unsafe MVC route. The tests obtain a token for the same identity that
submits the POST, and separately prove that a request without one is rejected.

## Capability before acknowledgment

Step 5 requires AsiBackbone `7.2.1` or later. In `7.2.0`, capability validation ran only when the policy decision could
proceed immediately. An acknowledgment-required decision cannot, so on the consequential endpoint the framework skipped
step 3 and issued the acknowledgment challenge directly: an administrator who did **not** hold
`referenceapplication.consequential.execute` received HTTP 428 instead of HTTP 403. The action body never executed, but
the actor was offered an acknowledgment for an operation they were not entitled to.

AsiBackbone `7.2.1` fixed this ([AsiBackbone/AsiBackbone#887](https://github.com/AsiBackbone/AsiBackbone/issues/887)).
Capability validation now runs for every decision that is not already denied, so a missing grant is denied before any
acknowledgment challenge. The validator needed no change for the upgrade, because it already denied a missing grant on
an acknowledgment-required decision. The upgrade is tracked in
[AsiBackbone/ReferenceApplication#17](https://github.com/AsiBackbone/ReferenceApplication/issues/17).

## Verification

`GovernedOperationsEndpointTests` exercises the production routes through the complete NCAT test host:

- the anonymous read-only description passes without governance metadata;
- an administrator is allowed and a non-administrator receives safe Problem Details;
- an actor with the exact normalized capability claim is allowed and an actor without it is denied;
- an administrator with the capability receives an HTTP 428 acknowledgment challenge and the consequential action
  body does not execute;
- an actor with the capability but without an administrator role is denied on the consequential endpoint;
- an administrator without the capability is denied on the consequential endpoint with safe Problem Details rather than
  offered an acknowledgment challenge, as described in
  [Capability before acknowledgment](#capability-before-acknowledgment);
- an anonymous caller is rejected by ASP.NET Core authorization before governance runs; and
- an otherwise-valid administrator request without an antiforgery token is rejected.

`ClaimEndpointCapabilityGrantValidator` is also tested on its own. It requires every declared scope, ignores grants on
unauthenticated identities, preserves an existing denial, and denies a missing grant even when the decision already
requires acknowledgment.

The complete inherited NCAT suite continues to run with the new tests:

```powershell
dotnet restore ReferenceApplication.slnx --locked-mode
dotnet build ReferenceApplication.slnx --configuration Release --no-restore
dotnet test ReferenceApplication.slnx --configuration Release --no-build --no-restore
```

## Authoritative references

- [AsiBackbone ASP.NET Core Endpoint Governance](https://asibackbone.github.io/AsiBackbone/articles/aspnetcore-endpoint-governance.html)
- [AsiBackbone Host-Owned Execution Enforcement](https://asibackbone.github.io/AsiBackbone/articles/host-owned-execution-enforcement.html)
- [NCAT Error Handling](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/error-handling.html)
- [NCAT Authorization](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/authorization.html)

## Next

[Step 6: Persist and verify audit evidence](06-persist-and-verify-audit.md)
