# Changelog

All notable changes to the AsiBackbone Reference Application are documented in this file.

This project follows Semantic Versioning using the format `MAJOR.MINOR.PATCH`. Completing the
[integration walkthrough](https://asibackbone.github.io/ReferenceApplication/walkthrough/) does not by itself declare
a stable public contract; releases remain `0.x` pre-releases until the repository's release-readiness criteria are met.
The release version is independent of the NCAT version, which the `ncat-v<version>` tags record.

## Unreleased

### Added

* A complete host-owned acknowledgment workflow for the consequential operation (#79).
  * HTTP 428 challenges are persisted in the existing AsiBackbone acknowledgment tables with a five-minute validity
    window. `POST /api/v1/acknowledgments` accepts the same actor's response and returns the one-time acknowledgment
    identifier used on the consequential retry.
  * Retry middleware binds the accepted acknowledgment to the same actor and endpoint operation and rejects malformed,
    expired, foreign, mismatched, or replayed uses with stable reason codes.
  * The acknowledged retry still passes through ordinary endpoint governance and capability validation. Signed
    single-use capability tokens are verified but not consumed by the initial challenge, then revalidated and consumed
    exactly once by the acknowledged retry.
  * The accepted decision ledger record carries `HandshakeId` and `AcknowledgmentId`; both participate in the canonical
    record hash and are exposed on the governance evidence timeline.
  * The governance explorer's Actions tab now performs the challenge, acknowledgment, and retry sequence rather than
    stopping at the challenge.
* Governance telemetry exported through OpenTelemetry (#78).
  * The host references `AsiBackbone.OpenTelemetry` `7.5.0` and registers its governance emitter with
    `UseOpenTelemetryEmission`. `GovernanceTelemetryProjection` projects each decision once its receipt is appended to
    the ledger, and each lifecycle event once the outbox delivery appends it. The result is an internal span in the
    `AsiBackbone.OpenTelemetry` source (for a decision, a child of the request's server span), plus the
    `asibackbone.governance.emissions` counter and `asibackbone.governance.emission_latency_ms` histogram, dimensioned
    only by event type, result, and provider.
  * The `ReferenceApplication.AuditReconciliation` meter is now collected, so its finding and outbox gauges are
    exported.
  * Envelopes are built field by field: the actor, reason codes, receipt and lifecycle metadata, request values, and
    mutation values never reach telemetry. A projection happens only after evidence is persisted, never throws, ignores
    the request's cancellation, and cannot change a governance outcome or delivery result.
  * The new `ReferenceApplication:OpenTelemetry:EnableGovernanceInstrumentation` setting, `true` by default, turns all
    of this on or off. Spans also need `EnableTracing`, and metrics `EnableMetrics`.
  * `ApplicationTelemetrySources` is the single list of collected sources and meters. Both the OpenTelemetry
    composition and the Development pipeline inspector read it, so the inspector now marks the governance source and
    meters as collected.
  * `LedgerDecisionReceiptSink` and `AsiBackboneAuditCompletionPublisher` take the projection as a new constructor
    parameter.
  * Step 7's new *Governance telemetry* section lists every span, tag, metric, dimension, and gauge, their cardinality,
    and what is never recorded. The README and *Run outside Development* link to it, and the stable release policy's
    #78 prerequisite now reads as completed.
  * Tests export through in-memory span and metric exporters added to the host's own providers. They cover the decision
    and lifecycle spans of a governed record, the governance metrics, the reconciliation gauges, the absence of
    sensitive values, and the disabled switch.
* A coverage gate in the `Build and test` job (#75). Coverage was previously reported only.
  * `scripts/Write-CoverageSummary.ps1` now fails the job when total line or branch coverage, or either production
    assembly's (`ReferenceApplication.Web` and `ReferenceApplication.Infrastructure`), falls below its floor. It also
    fails when a floored assembly is missing from the report or an assembly without a floor appears in it. It uses the
    same report and exclusions as before: generated migrations and the test assembly are excluded.
  * The floors live in `eng/code-coverage-floors.json`. Each is a baseline from the clean `main` run at `f2f03b3`,
    truncated to two decimals, minus a tolerance of 0.5 percentage points for the total and 1.0 per assembly:

    | Scope | Line baseline / floor | Branch baseline / floor |
    |:---|---:|---:|
    | Total | 96.00% / 95.50% | 78.20% / 77.70% |
    | `ReferenceApplication.Infrastructure` | 93.85% / 92.85% | 80.26% / 79.26% |
    | `ReferenceApplication.Web` | 96.75% / 95.75% | 77.61% / 76.61% |

  * Coverage is computed from covered and valid counts, not Cobertura's rounded rates, and a value exactly at its
    floor passes. The job summary shows each value next to its floor and lists every failure, and the
    `coverage-report` artifact is still published when the gate fails.
  * `-SelfTest` covers passing, exact-boundary, below-threshold, per-assembly, missing and duplicate assembly,
    missing and multiple report, invalid floors, and summary-file cases. CI runs it before the tests.
  * The README describes the gate and how to raise or lower a baseline. The stable release policy's #75 prerequisite
    now reads as completed.

* Release-to-release database upgrade tests and an upgrade, backup, and recovery procedure (#73). Upgrades are supported
  from `v0.1.0-alpha.11`.
  * `tests/ReferenceApplication.Web.Tests/Upgrades/v0.1.0-alpha.11/` holds two files:
    * `evidence.sql`, that release's representative evidence, captured through its own application paths: allowed and
      denied decisions, governed records with mutation audit, a delivered completion with its lifecycle event,
      reconciliation findings, and a used capability token; and
    * `migrations.sha256`, the hashes of the migrations it shipped.
  * `ReleaseUpgradeTests` check the following:
    * shipped migrations are unchanged;
    * a database at the boundary, loaded with the fixture and upgraded, keeps every table, column, row, and value;
    * a second upgrade changes nothing;
    * the upgraded schema matches a clean install; and
    * the upgraded application becomes ready and still verifies record hashes, links mutation audit and completions to
      decisions, keeps lifecycle events and capability-grant uses, and reconciles with nothing new.

    Destructive stand-ins are each detected: lost rows, a changed value that breaks a hash, and a dropped column.
  * The new CI job **Release upgrade test** builds the `v0.1.0-alpha.11` migration bundle from its tag and creates and
    seeds a database with it. It then upgrades the database twice with the current bundle, checks that the second run
    changes nothing, serves the database with the current image until ready, and runs the deep checks against it.
  * The new supplemental walkthrough page *Upgrade between releases* gives the supported boundary, the backup procedure
    (database and key ring), the upgrade steps, recovery from a failed upgrade, and rollback limits. Migrations are
    forward-only: never run older binaries against an upgraded database. The README links to it.
* A validated production deployment path (#72).
  * `docker-compose.production.yml` is a production-shaped reference configuration, applied on top of the base Compose
    file. It runs both services in `Production`, with these settings:
    * explicit `AllowedHosts` and a trusted proxy network, with the Development "trust every proxy" relaxation off;
    * an external OpenID Connect provider;
    * a durable Data Protection key ring encrypted with a certificate injected as a file secret;
    * every Development-only switch off; and
    * capability tokens disabled.

    It contains no credentials: every secret and deployment-specific value is required from the environment, and
    Compose refuses to start without one.
  * The CI container smoke test now also runs this configuration, with throwaway secrets generated on the runner. It
    checks the following:
    * the migrations service succeeds and the host becomes ready;
    * the host runs in `Production`, with no Development-only component registered and no relaxed-posture warning;
    * a request with an unlisted `Host` header is rejected;
    * the sign-in page offers the external provider and no demo identity; and
    * the stored key is encrypted with the injected certificate.
  * At startup the host now logs which Development-only components its service provider has registered. It logs
    `Security posture: no Development-only components are registered.` at Information when there are none, and a
    warning naming each one otherwise. An outside HTTP probe cannot show that a route is absent, because the fallback
    authorization policy sends every anonymous request to sign-in; this log line can.
  * `ProductionDeploymentTests` run the same settings in-process. They check readiness, the external provider, host
    filtering, and the encrypted key ring, and that no Development-only service is registered and no Development-only
    route is mapped. `ContainerConfigurationTests` guard the production Compose file.
  * The new supplemental walkthrough page *Run outside Development* is a single production checklist. It covers
    environment, host names, TLS and proxy trust, sign-in, secrets, Data Protection, migrations, health probes, logs,
    telemetry, evidence notifications, and capability-token signing. It also says what the reference configuration
    deliberately does not cover. The README links to it.

### Changed

* SQLite is now the only supported database provider (#74).
  * The SQL Server provider is no longer offered. `ReferenceApplication:DataAccess:Provider` accepts `Sqlite` or
    `None`/`Disabled`; any other value fails during service registration instead of when the first `DbContext` is
    resolved. `SqlServer` fails with an explicit message saying it is not supported and what adopting it requires.
  * The `Microsoft.EntityFrameworkCore.SqlServer` package reference, `DataAccessOptions.SqlServerProvider`, and the
    `ApplicationSqlServer` connection strings in `appsettings.json` and `appsettings.Development.json` are removed, along
    with the startup warning about a missing SQL Server migration history (event 19102).
  * `Microsoft.Bcl.AsyncInterfaces` 8.0.0 and `System.Configuration.ConfigurationManager` 9.0.11 are pinned centrally.
    The SQL Server provider's dependency graph had lifted them to those versions; without a pin, NuGet would now
    resolve the older minimums their remaining dependents declare.
  * The supplemental page *Create the Development Database* states the support policy and what an adopter must add
    to use another provider. The README, *Run outside Development*, and *Upgrade between releases* link to it.

### Fixed

* Reconciliation no longer reports the audit-completion outbox's own saves as missing completions (#87).
  * Before this fix, the NCAT save pipeline audited the outbox entry when it was staged and again when the dispatcher
    marked it delivered. Each save opened a mutation-audit batch that could never have a completion, so every
    delivered governed record produced two false Critical `MissingCompletion` findings.
  * The new `AuditBookkeepingRecordTypes` classifies outbox entries and reconciliation findings and remediations as
    audit bookkeeping. The save pipeline no longer writes mutation audit for them. It still canonicalizes and
    normalizes them, so no schema or migration changes.
  * Reconciliation skips retained batches that hold only bookkeeping rows, which earlier releases wrote. On a
    database upgraded from 0.1.0-alpha.11 it resolves the findings it reported for them the next time it runs.
    Missing completions for governed batches are still reported.
  * Step 6 of the walkthrough describes the exclusion and why it is made where audit rows are written.

## 0.1.0-alpha.11 - 2026-10-22

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
* The README and documentation now state the current NCAT baseline, 2.11.5 (#47).
  * The README still described the application only as generated from NCAT 2.11.4.
  * The documentation homepage's "Host baseline" row now names the version.
  * Step 1 calls 2.11.4 the original generation and points to the refresh guide, and its "current baseline tag" is
    now `ncat-v2.11.5`.
  * A documentation test requires the README, homepage, and Step 1 to match the refresh guide's target version.

### Added

* The governance sandbox is complete (#57): the load demonstration, policy simulator (#59), decision replay (#60),
  pipeline inspector (#61), and failure scenarios (#62), with signed capability tokens (#58) as their prerequisite.
  * Step 7 has a new *Governance sandbox* section. It says what each tool teaches and what it deliberately does not do,
    and lists the boundaries every tool keeps: Development only behind its own switch, administrator only with
    antiforgery protection, persisted evidence immutable, demonstration rather than benchmark, and no inline script or
    style. The failure scenarios also list what each one never does.
  * The documentation landing page, walkthrough index, and README describe the sandbox as a Development-only learning
    aid, not a production feature.
  * A new explorer test renders every sandbox tool together and checks that each is a labelled tab with its own output
    view and live status region, and that the page still has no inline script or style.
* Development-only, administrator-only failure scenarios in the governance explorer (#62), behind
  `ReferenceApplication:FailureScenarios:Enabled`, on a new **Failure scenarios** tab. They are registered only while
  the Development demo identity has selected the disposable demonstration database.
  * Eight presets run against the real governed endpoints: missing capability grant, constraint throws, ledger append
    fails, outbox delivery fails, execution fails after approval, evidence tampering, and, with signed capability tokens
    enabled, payload tampering under a valid token and expired token replay. While tokens are disabled, the last two are
    listed as unavailable with an explanation.
  * Each run shows the HTTP outcome, the recorded decision and reason codes, whether the governed action body ran, the
    mutation-audit and outbox evidence, and the request's evidence timeline. Scenario 4 shows the retry schedule and a
    **Clear the delivery fault** control; scenario 5 explains why reconciliation has nothing to report for a rolled-back
    mutation.
  * `POST /api/v1/failure-scenarios/{scenarioId}` arms a run for the current actor's next request, named by the
    `X-Failure-Scenario-Run` header. Middleware between authorization and endpoint governance consumes it, so a run is
    used once, by its own actor, within two minutes; any other use is refused with 409 before governance runs.
  * Faults are injected at the real failure points: the capability claim withheld from one request, a Development-only
    constraint that throws when armed, and an EF Core save interceptor that fails the ledger append, the governed
    record's save, or the lifecycle append for the armed request only. Evidence tampering verifies changed and unchanged
    in-memory copies of a stored decision; the stored row is never modified.
  * The scenario-2 reason code is AsiBackbone's `asibackbone.policy.constraint_exception`, verified against the package;
    the issue had proposed `policy.constraint_exception_as_denial`.
  * `GovernanceEvidenceVerifier.VerifyRecord` now holds the per-record hash check, so the explorer's verification and
    the tampering scenario use the same code.
* A Development-only, administrator-only pipeline inspector in the governance explorer (#61), behind
  `ReferenceApplication:PipelineInspector:Enabled`, on a new **Pipeline inspector** tab.
  * `GET /api/v1/pipeline-inspector/snapshot` returns the audit-completion outbox (health and the latest entries, with
    attempt counts, next attempts, errors, and whether each lifecycle event was appended), the reconciliation summary
    and open findings, the telemetry present in the process, and optionally one request's stage timing.
  * Stage timing uses timestamps the pipeline already stores, plus the request's server span when OpenTelemetry tracing
    created one, and marks the synchronous/asynchronous boundary. Nothing is added to the governed request path.
  * Telemetry comes from an in-process `ActivityListener` that never samples and a `MeterListener` that observes only
    what OpenTelemetry already collects and the host's own gauges, never from the OTLP exporter.
  * The explorer refreshes on evidence-stream hints and polls every 2 seconds while the tab is open and visible.
  * While the inspector is active, NCAT audit reconciliation is registered in Development, bound to
    `ReferenceApplication:AuditReconciliation`, with its worker running every minute. The host did not register it
    before.
  * Timings are labelled "Demonstration, not a benchmark" and link to the governance benchmarks.
* A Development-only, administrator-only decision replay in the governance explorer (#60), behind
  `ReferenceApplication:DecisionReplay:Enabled`, on a new **Decision replay** tab.
  * `GET /api/v1/decision-replay/decisions` lists persisted decisions newest first, 25 per page, and
    `GET /api/v1/decision-replay/decisions/{decisionReceiptId}` finds one; an unknown identifier returns not found. The
    lookup shows the recorded policy against the current one, the evidence-hash verification, and each evaluation
    input as recorded, inferred, or not recorded.
  * `POST /api/v1/decision-replay/decisions/{decisionReceiptId}/replay` replays a decision against the current policy
    and, optionally, a candidate: registered constraints left out, the stand-in policy 1.1.0, or both. Results are
    shown side by side with the recorded decision, with changed outcomes and added or removed reason codes marked.
  * The ledger does not record the actor's administrator role or capability grant. Replay infers them from the recorded
    outcome and reason codes where those determine them; where they do not, it evaluates each value and marks the
    replay partial when the result depends on it, rather than inventing a value. Signed tokens are never verified
    again: the recorded grant outcome is used.
  * A decision recorded under a different policy hash is flagged before replay, and its current-policy replay is
    labelled a what-if rather than a re-verification. Candidates are always labelled what-if.
  * Replay is read-only: the receipt is captured, never appended, and no evidence row is written or changed.
  * The policy simulator and decision replay now share `GovernancePipelineHarness`, which runs the endpoint governance
    pipeline against a simulated request and records each stage. `PolicySimulator` is now internal.
* A Development-only, administrator-only policy simulator in the governance explorer (#59), behind
  `ReferenceApplication:PolicySimulator:Enabled`.
  * The reader builds a request from fixed options: the governed operation, the simulated actor's administrator role
    and consequential capability, the acknowledgment flag, and a sample payload. Individual constraints can be left
    out, and the result is then labelled as simulated with a modified policy.
  * `POST /api/v1/policy-simulator/evaluate` runs AsiBackbone's `DefaultEndpointGovernanceService` against a simulated
    request for the real governed endpoint, with the policy evaluator composed exactly as the registered one. It
    reports each constraint's outcome and reason codes in evaluation order, the composed decision and the
    decision-policy step, the capability-grant check, the decision receipt that would be appended, and what the real
    endpoint would return.
  * With signed capability tokens enabled, the capability stage previews the grant `CapabilityTokenIssuer` would sign:
    its claims, payload binding, and validity window, with key identifiers but never key material. Nothing is signed
    and no use is consumed.
  * Simulations persist nothing: the receipt sink captures instead of writing, and the simulated request runs on an
    isolated asynchronous flow.
  * **Send for real** posts the same inputs through the real endpoint, when the policy is unmodified, the simulated
    actor matches the signed-in actor, and no capability token is needed, so the simulated receipt can be compared
    with the persisted decision in the evidence timeline.
  * The registered policy evaluator and the simulator now compose through `AsiBackboneServiceExtensions.CreatePolicyEvaluator`,
    and `CapabilityTokenIssuer.CreateGrant` builds the unsigned grant that `IssueAsync` signs.
  * The explorer now places its controls in tabs (Actions, Demo identity, Load demonstration, Policy simulator), each
    shown only when available, with the active tab's output in the right-hand panel: the evidence timeline, the live
    load metrics, or the staged simulation result.
* Signed capability tokens, disabled by default (#58). With `ReferenceApplication:CapabilityTokens:Enabled`, the
  capability-protected endpoints require a signed, request-bound token instead of accepting the permission claim alone.
  * Validation is AsiBackbone's `SignedCapabilityGrantEndpointValidator`, registered through
    `AddAsiBackboneSignedCapabilityGrants` with its form-field request binding. It replaces the claim validator only
    while tokens are enabled. Every rejection is denied with AsiBackbone's documented reason code.
  * `POST /api/v1/capability-tokens` issues a grant to an actor who holds the capability claim. The grant is bound to
    that actor, the governed operation and its scopes, the request's form fields, the policy version and hash, a short
    lifetime, and a single signed use. It is signed with `GovernanceArtifactSigner.SignCapabilityGrantAsync` and
    encoded as a `CapabilityGrantToken`.
  * Proofs are verified by AsiBackbone 7.4.0's `PublicKeySignatureVerificationService`, registered through
    `AddAsiBackboneKeySignatureVerification` with the public keys in `VerificationKeys`. Previous key versions keep
    verifying during a rotation, and a version marked `Revoked` is denied at once. No private key reaches verification.
  * Issuance signs with a PEM private key from configuration, never from source, through AsiBackbone's configured-key
    `LocalDevelopmentSigningService` (`CapabilityTokenSigningKey`). Startup fails when tokens are enabled without a
    usable signing key, or when no unrevoked `VerificationKeys` entry under `KeyVersion` matches it. In Production it
    also fails unless `AllowSampleSigningKeyInProduction` is set.
  * Replay protection uses AsiBackbone 7.4.0's `EfCoreCapabilityGrantUseStore` against `ApplicationDbContext`, so
    instances sharing the database accept a single-use token exactly once. Migration `AddCapabilityGrantUses` creates
    its table, and `CapabilityGrantUsePurgeService` purges records past their retention horizon every
    `UseStorePurgeIntervalSeconds`.
  * The authorizing token's identifier reaches the decision ledger's `CapabilityTokenId` through the decision receipt,
    covered by the record hash. The evidence timeline returns it as `capabilityTokenId`.
  * The supplemental walkthrough page compares the claim and token models and covers issuance, key rotation, and replay
    storage. A responsibilities table says what AsiBackbone provides once enabled and what the host must still supply,
    and a production wiring outline shows that only the issuance signing key still needs replacing.
    `AddApplicationCapabilityTokens` marks each of the same six steps in code as library or host. The page links to
    AsiBackbone's article for the library behavior.
  * This replaces the host-side prototype built against 7.2.1, which is not merged, and the interim 7.3.0 wiring: no
    host validator, token codec, signature verifier, key ring, use store, ledger side channel, or HMAC provider is
    added, and `AsiBackbone.Storage.InMemory` is no longer referenced.
* A CI container smoke test that, on every pull request:
  * builds both images;
  * proves readiness against the migrated `ApplicationDatabase` and against the demonstration database;
  * re-applies migrations to confirm they are idempotent.
* The container smoke test also inspects both images (#46):
  * the runtime image runs as a non-root user and contains no .NET SDK or migration bundle;
  * the migrations image runs as a non-root user and contains an executable bundle.
* The versioned `main` ruleset now requires the container smoke test (#46), so a change that breaks the Dockerfile,
  Compose file, project files, or lock files cannot merge.
* A repository controls test that fails when any CI job is not a required status check in the versioned `main`
  ruleset, or when a required check is duplicated or not bound to GitHub Actions.
* Code coverage reporting in CI (#49). The `Build and test` job now uses the previously unused `coverlet.MTP`
  extension to collect Cobertura coverage, excluding generated EF Core migrations. It writes line and branch totals
  per assembly to the job summary with `scripts/Write-CoverageSummary.ps1` and uploads the `coverage-report`
  artifact. Coverage is reported, not gated; at introduction it is 94.9% of lines and 79.7% of branches.
* A repository controls test that fails if CI stops collecting, summarizing, or publishing coverage.

### Changed

* Updated the AsiBackbone packages from 7.4.0 to 7.5.0 and refreshed the package lock files. Policy-evaluated endpoint
  decisions now record the request's trace identifier
  ([AsiBackbone/AsiBackbone#897](https://github.com/AsiBackbone/AsiBackbone/issues/897)), so the pipeline inspector
  (#61) links a request to its spans through the decision's own ledger record. The workaround that looked up the trace
  through the span recorded by the NCAT mutation audit is removed, and a governed request that changes no data is now
  linked to its spans too.
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
