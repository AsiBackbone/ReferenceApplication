# Changelog

All notable changes to the AsiBackbone Reference Application are documented in this file.

This project follows Semantic Versioning using the format `MAJOR.MINOR.PATCH`. Releases are `0.x` pre-releases while
the [integration walkthrough](https://asibackbone.github.io/ReferenceApplication/walkthrough/) is being written. The
release version is independent of the NCAT version, which the `ncat-v<version>` tags record.

## 0.1.0-alpha.5 - 2026-10-18

Completes walkthrough [Step 5: Govern Endpoints](https://asibackbone.github.io/ReferenceApplication/walkthrough/05-govern-endpoints.html).

### Added

* A versioned `/api/v1/governed-operations` sample API with three antiforgery-protected POST operations:
  * an administrator-governed operation;
  * a capability-protected operation; and
  * a consequential operation that combines administrator, capability, and acknowledgment requirements.
* A host-owned endpoint capability validator that requires every declared capability scope to appear as an exact
  normalized application permission claim on an authenticated identity.
* End-to-end tests for the ungoverned read-only route, administrator and capability allow/deny paths, the
  acknowledgment challenge, combined administrator-and-capability requirements on the consequential endpoint,
  anonymous rejection by authorization, and antiforgery enforcement.
* Validator tests for grants on unauthenticated identities and for acknowledgment-required decisions.

### Changed

* Governance denials now use the application's safe Problem Details shape while preserving HTTP 403 semantics.
* The governed POST actions validate antiforgery tokens explicitly with `[ValidateAntiForgeryToken]`.
* The landing page reports 5 of 6 walkthrough steps complete.

### Known issues

* On AsiBackbone 7.2.0, an administrator **without** the capability grant receives an HTTP 428 acknowledgment
  challenge on the consequential endpoint instead of HTTP 403, because the framework skips capability validation for
  acknowledgment-required decisions. The action still does not execute. Tracked in
  [AsiBackbone/AsiBackbone#887](https://github.com/AsiBackbone/AsiBackbone/issues/887) and
  [#17](https://github.com/AsiBackbone/ReferenceApplication/issues/17).

## 0.1.0-alpha.4 - 2026-10-18

Completes walkthrough [Step 4: Define Governance Rules](https://asibackbone.github.io/ReferenceApplication/walkthrough/04-define-governance-rules.html).

### Added

* A host-owned sample governance policy set under `src/ReferenceApplication.Web/Governance`, identified by policy
  version `1.0.0` and a SHA-256 hash of its canonical definition. Both values are written into every endpoint
  governance decision.
* Three sample rules, each recognized by a policy marker type:
  * `AdministratorActionPolicy`: the authenticated actor must hold a configured administrator role
    (`referenceapplication.actor.administrator_required`).
  * `ConsequentialActionPolicy`: an otherwise-allowed operation requires explicit acknowledgment
    (`referenceapplication.consequential.acknowledgment_required`).
  * `CapabilityProtectedActionPolicy`: the endpoint must declare the `referenceapplication.consequential.execute`
    capability scope (`referenceapplication.capability.scope_required`).
* A scoped AsiBackbone policy evaluator composed from those rules. Endpoints still opt in through policy markers;
  none of the application's own endpoints carry them until Step 5.
* Unit tests for each rule and the composed evaluator, and enforcement tests that run each rule through AsiBackbone's
  real endpoint metadata and middleware, so a metadata-format mismatch cannot silently disable the rules.

### Changed

* The landing page reports 4 of 6 walkthrough steps complete.
* An endpoint with governance metadata but no applicable rule is now evaluated and allowed. In Step 3 it was blocked
  because no policy evaluator was registered.

## 0.1.0-alpha.3 - 2026-10-18

Completes walkthrough [Step 3: Register AsiBackbone Services](https://asibackbone.github.io/ReferenceApplication/walkthrough/03-register-asibackbone-services.html).

### Added

* `AddApplicationAsiBackbone` registers AsiBackbone's ASP.NET Core integration services and maps the governance actor
  from the application's normalized claims: `application:subject` for the actor identifier and `application:name` for
  the display name. AsiBackbone's standard identifier and display-name claims remain fallbacks.
* The middleware pipeline calls `UseAsiBackboneEndpointGovernance(requireEndpointRoutingRegistered: true)` after
  routing, authentication, and authorization, and before endpoints execute. Endpoint governance remains opt-in, so
  endpoints without governance metadata are unchanged.
* Tests for the AsiBackbone service registration, actor mapping, and middleware placement, including an end-to-end
  check that an authenticated request resolves to the actor from the normalized claims.
* Tests that guard the startup exception filter described under *Fixed*.

### Changed

* `/` now serves a ReferenceApplication landing page from `HomeController.Index`, with explicit anonymous access,
  replacing the NCAT template's Razor Page. Tests that used `/` as a convenient endpoint now use dedicated test
  endpoints, and the NCAT test for fallback authorization on an unannotated Razor Page no longer applies.

### Fixed

* EF Core design-time commands such as `dotnet ef migrations list` and `dotnet ef database update` no longer log a
  spurious fatal "application terminated unexpectedly" error. The startup `catch` in `Program.cs` now excludes the
  `HostAbortedException` those commands throw on purpose; genuine startup failures are still logged as fatal and
  rethrown.

## 0.1.0-alpha.2 - 2026-10-18

Completes walkthrough [Step 2: Add AsiBackbone Packages](https://asibackbone.github.io/ReferenceApplication/walkthrough/02-add-asibackbone-packages.html).

### Added

* AsiBackbone 7.2.0 packages under central package management, with locked dependency graphs:
  `AsiBackbone.Core`, `AsiBackbone.AspNetCore`, `AsiBackbone.EntityFrameworkCore`, `AsiBackbone.Analyzers`
  (build-only), and `AsiBackbone.Testing`. No services or middleware are registered.

## 0.1.0-alpha.1 - 2026-10-18

First pre-release: the NCAT 2.11.5 baseline with walkthrough
[Step 1: Scaffold from NCAT](https://asibackbone.github.io/ReferenceApplication/walkthrough/01-scaffold-from-ncat.html)
complete.

### Added

* An ASP.NET Core host generated from the .NET Core Application Template 2.11.5. The unmodified scaffold is kept on
  the protected `ncat-baseline` branch and tagged `ncat-v2.11.5`.
* The documentation site with the generated API reference and the integration walkthrough.
* Repository automation and controls: CI, CodeQL, OWASP Dependency-Check, workflow security analysis, OpenSSF
  Scorecard, strict DocFX builds, and versioned branch rulesets.

### Fixed

* Suppressed cross-ecosystem OpenTelemetry CVE false positives in dependency scanning (#10, #12).
