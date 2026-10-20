# Changelog

All notable changes to the AsiBackbone Reference Application are documented in this file.

This project follows Semantic Versioning using the format `MAJOR.MINOR.PATCH`. Completing the
[integration walkthrough](https://asibackbone.github.io/ReferenceApplication/walkthrough/) does not by itself declare
a stable public contract; releases remain `0.x` pre-releases until the repository's release-readiness criteria are met.
The release version is independent of the NCAT version, which the `ncat-v<version>` tags record.

## Unreleased

### Fixed

* Docker Compose now creates and migrates `ApplicationDatabase` (#45). Before this, the default Compose run worked only
  because the Development demo identity redirected data access to a demonstration database inside the container's
  writable layer. Disabling the demo, or running outside Development, left the host unready, and governed operations
  failed closed.
  * A new `migrations` Dockerfile target packages the checked-in migrations as an EF Core migration bundle.
  * A one-shot `referenceapplication.migrations` Compose service applies it before the web service starts.
  * The application still never migrates `ApplicationDatabase` at startup.
  * The demonstration database now lives on the data volume, and `REFERENCEAPPLICATION_DEMOIDENTITY_ENABLED` selects
    between the two databases.
* Development now writes its rolling log to `Logs/application-web-.log`, the same name as the base settings, instead
  of the `Logs/template-web-.log` name inherited from the NCAT 2.11.5 scaffold (#48). A settings test now requires
  every Serilog file sink in the web project to use the application log name.

### Added

* A CI container smoke test that, on every pull request:
  * builds both images;
  * proves readiness against the migrated `ApplicationDatabase` and against the demonstration database;
  * re-applies migrations to confirm they are idempotent.

### Changed

* The governance evidence notification stream now writes a `: keep-alive` comment every 15 seconds while idle, so
  proxies and load balancers do not close an open explorer's stream. Heartbeats and events share one writer lock, so
  SSE frames never interleave.

### Tests

* Unit tests for the in-memory evidence notifier: fan-out to every subscriber in order, drop-oldest at the 32-hint
  capacity, removal on disposal, and repeated disposal.
* Unit tests for the notification publisher: a failing notifier is contained and logged with identifiers only.
* Controller tests for exact, case-sensitive correlation filtering, the unfiltered stream, heartbeat cadence, and
  whole-frame output under concurrent writes.
* Host tests proving every evidence endpoint rejects a correlation identifier longer than 128 characters.

## 0.1.0-alpha.10 - 2026-10-19

### Added

* An administrator-only, Development-only bounded governance load demonstration with live Server-Sent Events updates,
  cancellation, single-run concurrency, and enforced iteration and duration ceilings.
* Transient throughput and decision-latency histogram feedback, explicitly labelled as a demonstration rather than a
  benchmark and linked to the reproducible BenchmarkDotNet results.
* Walkthrough Step 8, which records the repeatable host-governance benchmark procedure, reference results, measurement
  boundary, and relationship to AsiBackbone's framework benchmark baseline.

### Changed

* The walkthrough index, README, documentation homepage, and application landing page now report all eight integration
  steps complete and direct readers to the Step 7 explorer and Step 8 measurements.

### Security

* The load demonstration makes no HTTP self-requests, is absent outside Development, requires antiforgery protection,
  and persists no synthetic governance evidence.

## 0.1.0-alpha.9 - 2026-10-19

Adds the interactive governance evidence explorer that brings the Step 7 observability work together in one runnable
development experience.

### Added

* An authenticated, two-pane governance explorer with administrator, capability, consequential-action,
  record-creation, and evidence-verification controls.
* Live decision, mutation-audit, outbox, and lifecycle updates through the governance evidence Server-Sent Events
  endpoint, with administrator-only access to the full evidence ledger.
* Accessible status updates, keyboard focus treatment, responsive styling, walkthrough guidance, and regression
  coverage for the explorer workflow.

## 0.1.0-alpha.8 - 2026-10-19

Adds live governance evidence notifications and tamper-evident decision records.

### Added

* A bounded in-process governance evidence notifier and administrator-only Server-Sent Events endpoint with optional
  correlation filtering. Notifications are best effort and cannot affect governance decisions or outbox delivery.
* Canonical payload hashes for new governance decision ledger records and an administrator-only verification endpoint
  that reports verified, mismatched, or legacy not-hashed evidence.
* SQLite-backed coverage for stream ordering and isolation, access control, SSE formatting, untouched and tampered
  evidence, legacy records, and unknown correlations.

### Changed

* Governance decision, mutation-audit, and lifecycle persistence now publish minimized evidence notifications after
  successful storage.

## 0.1.0-alpha.7 - 2026-10-18

Begins Step 7 by making the persisted governance evidence observable through an API and safe development identity.

### Added

* An administrator-only governance evidence query endpoint keyed by correlation identifier, returning a minimized,
  chronological timeline across decision, application, NCAT audit, outbox, and lifecycle evidence.
* An opt-in Development-only demo identity that issues fixed administrator and capability claims through the normal
  cookie, authorization, antiforgery, and governance pipeline.
* Migrated SQLite integration coverage for allowed and denied decisions, unknown correlations, access control,
  timeline ordering, payload minimization, and enabled and disabled demo-identity environments.

### Changed

* The README now tracks the expanded Step 7 observability work and Step 8 governance-overhead benchmarks.

## 0.1.0-alpha.6 - 2026-10-18

Completes walkthrough [Step 6: Persist and Verify Audit Evidence](https://asibackbone.github.io/ReferenceApplication/walkthrough/06-persist-and-verify-audit.html).
All six walkthrough steps are now complete.

### Added

* AsiBackbone's EF Core model in the host-owned `ApplicationDbContext`, with the host-owned
  `AddGovernedAuditEvidence` migration. The migration creates the AsiBackbone audit ledger, reason-code, metadata,
  acknowledgment, outbox, and lifecycle tables and a `GovernedOperationRecords` table, and alters no existing table.
* `LedgerDecisionReceiptSink`, which persists every endpoint governance decision receipt to the AsiBackbone audit ledger
  before the endpoint executes and fails closed when the record cannot be stored.
* Request-scoped governance decision evidence. NCAT's HTTP audit context uses it, so every mutation in a governed
  request carries the decision audit record identifier, an operation execution identifier, and the decision's
  correlation and trace identifiers.
* `AsiBackboneAuditCompletionPublisher`, the host-owned form of AsiBackbone's NCAT audit-completion adapter. It turns
  NCAT audit-completion outbox entries into AsiBackbone lifecycle events linked to the decision: idempotent and
  conflict-checked, deferred until the decision exists, and rejecting mismatched correlation or trace identifiers.
* `POST /api/v1/governed-operations/records`, an administrator-governed operation that creates a record inside NCAT's
  audited transaction and stages its audit completion in the same transaction.
* `AddApplicationGovernanceEvidence`, which enables NCAT's audit-completion outbox with the AsiBackbone destination.
* End-to-end tests against migrated SQLite databases for the full evidence chain, denials, decision outcomes,
  anonymous and ungoverned requests, invalid input, and failing closed. Also tests for the model and migrations,
  evidence preservation, the sink, the publisher, and the audit context, plus edge-case tests for the Step 4 rules and
  the Step 5 capability validator.

### Changed

* NCAT's save pipeline and UTC timestamp convention now treat NCAT audit records and AsiBackbone persistence entities as
  accountability evidence. Evidence is stored exactly as created: no string canonicalization, timestamp normalization,
  or mutation auditing. Application data is unaffected.
* The governed POST operations emit governance audit, so allowed decisions, denials, and acknowledgment challenges are
  all recorded.
* The governed-operation endpoint tests run against a migrated database, because governed operations now require
  durable decision evidence.
* The landing page, README, and documentation homepage report all six walkthrough steps complete. The homepage's
  governance-rules route no longer describes the rules as planned.
* The Step 6 page and the development-database guide now tell readers of this repository to apply the included
  `AddGovernedAuditEvidence` migration with `database update`, and present `migrations add` only as how the migration
  was created. Running `migrations add` in a clone fails because the migration already exists.

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
  acknowledgment challenge, combined administrator-and-capability requirements on the consequential endpoint
  (including denial of an administrator without the capability grant), anonymous rejection by authorization, and
  antiforgery enforcement.
* Validator tests for grants on unauthenticated identities and for acknowledgment-required decisions.

### Changed

* Updated the AsiBackbone packages (`AsiBackbone.Core`, `AsiBackbone.AspNetCore`, `AsiBackbone.EntityFrameworkCore`,
  `AsiBackbone.Analyzers`, and `AsiBackbone.Testing`) from 7.2.0 to the 7.2.1 patch release and regenerated the lock
  files. Walkthrough Step 2 now installs 7.2.1.
* Governance denials now use the application's safe Problem Details shape while preserving HTTP 403 semantics.
* The governed POST actions validate antiforgery tokens explicitly with `[ValidateAntiForgeryToken]`.
* The landing page reports 5 of 6 walkthrough steps complete.

### Notes

* Step 5 requires AsiBackbone 7.2.1 or later. On 7.2.0, the framework skipped capability validation for
  acknowledgment-required decisions, so an administrator **without** the capability grant received an HTTP 428
  acknowledgment challenge on the consequential endpoint instead of HTTP 403. AsiBackbone 7.2.1 validates capability
  grants for every decision that is not already denied
  ([AsiBackbone/AsiBackbone#887](https://github.com/AsiBackbone/AsiBackbone/issues/887)), so this release denies that
  request ([#17](https://github.com/AsiBackbone/ReferenceApplication/issues/17)).

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
