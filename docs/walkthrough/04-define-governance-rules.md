---
title: "Step 4: Define Governance Rules"
description: The host-owned sample policy set, stable decision identity, registration, and isolated verification.
---

# Step 4: Define Governance Rules

**Status:** Complete with AsiBackbone `7.2.0`. Released in [`v0.1.0-alpha.4`](https://github.com/AsiBackbone/ReferenceApplication/releases/tag/v0.1.0-alpha.4).

## Outcome

The host owns a small, explicit governance policy set under
`src/ReferenceApplication.Web/Governance`. The rules demonstrate three different decision shapes without executing
the protected operation:

| Policy marker | Rule | Outcome | Stable reason code |
|:---|:---|:---|:---|
| `AdministratorActionPolicy` | The current authenticated actor must hold one of the administrator roles configured by the host. | Allow or deny | `referenceapplication.actor.administrator_required` |
| `ConsequentialActionPolicy` | An otherwise-allowed consequential operation requires an explicit acknowledgment before execution. | Acknowledgment required | `referenceapplication.consequential.acknowledgment_required` |
| `CapabilityProtectedActionPolicy` | The endpoint must declare the `referenceapplication.consequential.execute` capability scope. | Allow or deny | `referenceapplication.capability.scope_required` |

The capability rule verifies the policy-to-endpoint contract only. It does not treat declared metadata as proof that an
actor has a valid grant. Step 5 attaches the marker and scope to a consequential endpoint and supplies the host-owned
grant validation boundary before execution.

## Stable policy identity

`ReferenceApplicationGovernancePolicySet` declares policy version `1.0.0` and computes a SHA-256 hash from a canonical
definition of the three rules. `EndpointGovernanceOptions` carries both values into every decision produced for an
ASP.NET Core endpoint. Changing the meaning of a rule therefore requires an intentional version and reviewed-hash
change rather than silently changing decision receipts.

The policy set also centralizes the capability scope so endpoint metadata and validation code do not duplicate a
security-sensitive string.

## Rule boundaries

`AdministratorRoleConstraint` reads the current request actor from `IHttpContextAccessor`, but it uses the same
`ApplicationAuthorizationOptions` role claim type and administrator values as NCAT authorization. Authentication and
claim normalization remain host-owned; the governance rule does not invent a second role model.

`CapabilityScopeDeclarationConstraint` reads only sanitized framework-neutral endpoint metadata. It fails closed when
the capability policy marker is present without its required scope, catching an incomplete Step 5 endpoint declaration.

`ConsequentialActionDecisionPolicy` runs after constraint composition. It preserves any existing blocking decision and
only converts a decision that could otherwise proceed into `AcknowledgmentRequired`. The rule creates no challenge and
performs no operation; those remain ASP.NET Core host responsibilities.

Each rule first checks `endpoint.policy_types` and returns `NotApplicable` when its marker is absent. This matters
because AsiBackbone runs every registered constraint for a governed endpoint; a marker identifies host intent but does
not select constraints automatically.

The rules read two entries from the endpoint metadata that AsiBackbone generates: `endpoint.policy_types`, which lists
the full type names of the endpoint's policy markers, and `endpoint.capability_scopes`, which lists its declared
capability scopes. Keep `EndpointGovernanceOptions.MetadataMode` at its default, `Full`. `Reduced` mode keeps the
policy markers but drops the capability scopes, so the capability rule would reject every capability-protected
endpoint, including correctly declared ones.

## Evaluator registration

`AddApplicationAsiBackbone` registers both constraints, the consequential decision policy, and a scoped
`IGovernancePolicyEvaluator<GovernanceEvaluationContext>`. The evaluator is scoped because the administrator rule reads
the current request actor. It uses AsiBackbone's default fail-closed evaluator behavior and writes the policy version
and hash configured above into its decisions.

```csharp
services.AddScoped<IGovernanceConstraint<GovernanceEvaluationContext>, AdministratorRoleConstraint>();
services.AddScoped<IGovernanceConstraint<GovernanceEvaluationContext>, CapabilityScopeDeclarationConstraint>();
services.AddScoped<IGovernanceDecisionPolicy<GovernanceEvaluationContext>, ConsequentialActionDecisionPolicy>();
```

Endpoints still opt in through policy metadata. Step 4 deliberately does not attach these markers to application
endpoints; that enforcement wiring belongs to Step 5.

## Verification

`GovernancePolicyTests` verifies:

- the reviewed hash for policy version `1.0.0`;
- administrator allow, deny, and not-applicable paths;
- capability-scope declaration allow and deny paths;
- acknowledgment-required composition for consequential operations;
- preservation of a prior denied decision; and
- composed evaluator results when several markers apply.

`GovernanceEnforcementTests` verifies the rules against AsiBackbone's real endpoint metadata and middleware, so a
mismatch cannot silently turn every rule into `NotApplicable`:

- metadata generated for a real endpoint identifies its policy markers and declared capability scope;
- the host keeps `Full` metadata mode;
- an administrator endpoint blocks non-administrator and anonymous callers before the action runs, and executes for an
  administrator;
- a consequential endpoint does not execute without acknowledgment;
- an endpoint with the capability-protected marker but no declared scope is rejected; and
- governance does not replace antiforgery protection: the test endpoints are antiforgery-validated POSTs, like Step 5's
  consequential endpoints, and an administrator's request without a valid token is still rejected.

`AsiBackboneServiceExtensionsTests` and `AsiBackboneIntegrationTests` verify that the evaluator and the policy version
and hash are registered through the complete application service composition.

The complete inherited NCAT suite runs alongside those tests:

```powershell
dotnet restore ReferenceApplication.slnx --locked-mode
dotnet build ReferenceApplication.slnx --configuration Release --no-restore
dotnet test ReferenceApplication.slnx --configuration Release --no-build --no-restore
```

## Authoritative references

- [AsiBackbone Custom Decision Policy Examples](https://asibackbone.github.io/AsiBackbone/articles/custom-decision-policy-examples.html)
- [AsiBackbone Policy Evaluator Pipeline](https://asibackbone.github.io/AsiBackbone/articles/policy-evaluator-pipeline.html)
- [AsiBackbone ASP.NET Core Endpoint Governance](https://asibackbone.github.io/AsiBackbone/articles/aspnetcore-endpoint-governance.html)
- [AsiBackbone Core Domain Language](https://asibackbone.github.io/AsiBackbone/articles/core-domain-language.html)
- [NCAT Authorization](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/authorization.html)

## Next

[Step 5: Govern endpoints](05-govern-endpoints.md)
