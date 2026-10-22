---
title: "Stable Release Policy v1"
description: Versioned compatibility contract, graduation gates, and release procedure for the first stable Reference Application release.
---

# Stable Release Policy v1

**Policy version:** 1.0

**Applies to:** the first stable `1.x` release and later `1.x` releases unless this policy is superseded by a new versioned policy.

This document turns the move from the `0.x` alpha series to a stable release into a repeatable repository decision. A
stable release is not authorized merely because the walkthrough is complete or because a single CI run is green. The
exact merged release commit must satisfy the compatibility contract, graduation gates, and release procedure below.

## Stable compatibility contract

Semantic Versioning applies to the surfaces explicitly listed as stable here. A change to one of these surfaces that
breaks an existing supported consumer requires a new major version unless the affected surface has first completed the
deprecation process described below.

### Stable HTTP surface

The following non-Development HTTP routes are stable for `1.x` once the first stable release is published:

| Surface | Stable contract |
| --- | --- |
| `GET /api/v1/application-information` | Route, successful response shape, API-version meaning, and documented deprecation headers. |
| `GET /api/application-information` | Default-version alias for application information while the v1 API remains supported. |
| `GET /api/v1/governed-operations` | Read-only sample description route and response shape. |
| `POST /api/v1/governed-operations/administrator` | Route, authentication/authorization boundary, governance outcome semantics, and documented response status behavior. |
| `POST /api/v1/governed-operations/capability` | Route, capability requirement, governance outcome semantics, and documented response status behavior. |
| `POST /api/v1/governed-operations/records` | Route, audited-mutation behavior, and `GovernedRecordCreatedResponse` fields linking the record, decision, execution, and mutation batch. |
| `GET /api/v1/governance-evidence/{correlationId}` | Administrator-only minimized evidence timeline contract. |
| `GET /api/v1/governance-evidence/{correlationId}/verification` | Administrator-only decision-record hash verification contract. |
| `GET /api/v1/governance-evidence/notifications` | Administrator-only identifier-hint SSE contract; the stream remains best-effort rather than authoritative. |
| `POST /api/v1/capability-tokens` | When capability tokens are enabled, the issuance route, authorization requirement, single-use intent, payload binding, and response fields. |
| `/health`, `/health/live`, `/health/ready`, `/health/audit-integrity` | Route purpose and status semantics documented by the application. These are operational endpoints, not application business APIs. |

The `POST /api/v1/governed-operations/consequential` demonstration is **not part of the first stable compatibility
surface unless #79 is completed before the release**. The current sample stops at the acknowledgment challenge. If #79
is not completed, the route remains a documented sample-only demonstration and may change in a `1.x` release when the
complete acknowledgment workflow is added.

Development-only sandbox APIs are not stable HTTP surfaces. This includes the load demonstration, policy simulator,
decision replay, pipeline inspector, failure scenarios, demo-identity controls, and demonstration-database reset.

### Stable reason-code contract

Reason codes emitted by the application or persisted in evidence are stable when they are:

1. defined by `ReferenceApplication` for a non-Development stable API or persistence path; or
2. explicitly documented by this repository as an AsiBackbone/NCAT reason code on which the Reference Application
   relies.

Existing meaning must not be reassigned to a stable reason code. New reason codes may be added in a minor release.
Removing or changing the meaning of a stable reason code is breaking unless it completes the deprecation process.

Reason codes produced only by Development failure scenarios, policy-simulator experiments, replay candidates, or other
sandbox-only behavior are sample-only.

### Stable configuration contract

The following configuration section names and their documented production meanings are stable for `1.x`:

- `ConnectionStrings:ApplicationDatabase`;
- `ReferenceApplication:Authentication`;
- `ReferenceApplication:Authorization`;
- `ReferenceApplication:DataAccess`;
- `ReferenceApplication:DataProtection`;
- `ReferenceApplication:ForwardedHeaders`;
- `ReferenceApplication:RateLimiting`;
- `ReferenceApplication:HealthChecks`;
- `ReferenceApplication:OpenTelemetry`;
- `ReferenceApplication:AuditCompletionOutbox`;
- `ReferenceApplication:AuditReconciliation`; and
- `ReferenceApplication:CapabilityTokens` when the optional signed-token feature is enabled.

A new key may be added compatibly. Removing a documented key, changing its type, or changing its production meaning is
breaking unless deprecated first.

Development-only sections such as `DemoIdentity`, `GovernanceLoadDemonstration`, `PolicySimulator`, `DecisionReplay`,
`PipelineInspector`, and `FailureScenarios` are sample controls and are not stable configuration contracts.

### Stable persisted evidence and schema contract

For the supported SQLite database, `1.x` guarantees forward migration from the documented supported upgrade boundary
and preservation of retained application/governance evidence covered by the release-upgrade tests. Stable persisted
surfaces include:

- governed operation records;
- NCAT mutation-audit records and mutation manifests;
- AsiBackbone decision ledger records and their hashes;
- decision reason-code and metadata records;
- audit-completion outbox state;
- decision lifecycle events;
- reconciliation findings and remediation records; and
- capability-grant use records when signed capability tokens are enabled.

Checked-in migration files that have shipped in a release are immutable. Schema evolution is forward-only. A stable
release does not promise that older binaries can run against a database upgraded by a newer release.

Internal EF Core entity implementation details that do not affect the persisted schema, evidence meaning, or supported
upgrade path are not compatibility surfaces.

### Stable extension points

The stable extension boundary is the documented host integration surface used by the walkthrough: ASP.NET Core
configuration, dependency-injection registration, the host-owned `ApplicationDbContext`, documented NCAT/AsiBackbone
interfaces, and supported override seams explicitly described by the walkthrough.

Private/internal classes, test helpers, benchmark fixtures, Razor/JavaScript implementation details, CSS selectors, and
Development sandbox services are not stable extension points.

## Supported environment matrix

The first stable release may claim only the matrix below unless the policy is revised with corresponding CI evidence.

| Dimension | Stable support |
| --- | --- |
| .NET target | `.NET 10` / `net10.0`. |
| Build SDK | `global.json` pinned SDK family (`10.0.400` with `latestPatch` roll-forward at the time this policy was adopted). |
| Container runtime | The checked-in Linux .NET 10 container images built by the repository Dockerfile. |
| CI reference OS | Ubuntu 24.04 GitHub-hosted runner for build, test, container, security, and upgrade validation. |
| Database provider | SQLite only. Selecting SQL Server or another provider is unsupported by this repository unless a later policy adds provider-specific migration and integration coverage. |
| Production example | `docker-compose.production.yml` layered over `docker-compose.yml`; it is a reference configuration, not a hosted product or hosting recommendation. |
| Database upgrade boundary | `v0.1.0-alpha.11` until a later release deliberately advances the tested boundary. |
| Topology | A single application instance is fully demonstrated. Multiple replicas require a shared database and Data Protection key ring; process-local evidence notifications require sticky routing or an application-supplied shared backplane for cross-replica freshness hints. |

Windows remains a valid development environment, but a stable production-OS support claim is not made until it is
continuously exercised by release CI.

## Semantic versioning and deprecation

For stable releases:

- **PATCH** fixes defects without intentionally changing stable contracts.
- **MINOR** may add stable endpoints, fields, configuration keys, reason codes, or optional behavior while preserving
  existing stable consumers.
- **MAJOR** is required for incompatible changes to a stable surface.

A planned removal from a stable surface must:

1. be documented in the changelog and relevant public documentation;
2. expose a replacement or migration path where one exists;
3. remain supported for at least one subsequent minor release unless an urgent security issue makes that unsafe; and
4. be removed only in a major release, except when maintaining it would preserve a confirmed vulnerability.

The existing `0.9` application-information API is already marked deprecated and is pre-stable history; the stable
contract begins with v1.

## Explicitly non-stable/sample-only surfaces

The following are outside the `1.x` compatibility guarantee unless a later versioned policy promotes them:

- all Development-only governance sandbox routes and controls;
- the disposable demonstration database and fixed demo identity;
- benchmark numbers and machine-specific performance results;
- benchmark implementation details and fixture layout;
- HTML/CSS presentation details of the governance explorer;
- test-only controllers, authentication handlers, clocks, publishers, fixtures, and failure injection;
- internal telemetry-listener implementation used by the Development inspector;
- undocumented internal classes or DI registrations; and
- third-party framework/package behavior beyond the versions and integration assumptions explicitly documented here.

The consequential acknowledgment demonstration is also sample-only until #79 is either completed and promoted by a
policy update or explicitly retained as a challenge-only teaching example.

## Graduation gates for the first stable release

Every item below is blocking unless it names an exception path.

### Repository prerequisites

- [ ] #72 remains completed: Production-shaped startup/deployment validation is present and green.
- [ ] #73 remains completed: release-to-release SQLite upgrade evidence and recovery guidance are present and green.
- [ ] #74 remains completed: SQLite is the only supported database provider and code/docs/CI agree.
- [ ] #75 remains completed: repository-owned line and branch coverage floors protect the tested baseline.
- [ ] #77 remains completed: reconciliation is registered independently of the Development pipeline inspector.
- [ ] #81 is completed: Step 2's current AsiBackbone package/version statements are correct and test-guarded.
- [ ] #78 remains completed: AsiBackbone governance spans and metrics and the reconciliation gauges are exported
      through OpenTelemetry, tested with in-memory exporters, and documented.
- [ ] #79 is completed **or** the release keeps the consequential acknowledgment flow sample-only as this policy
      currently defines it.
- [ ] #82 is completed before benchmark results are used as stable-release qualification evidence. It is not otherwise
      a blocker because performance numbers are not a stable compatibility surface.

### Exact-commit quality gates

Record the exact release commit SHA and links to successful hosted runs for that SHA. At minimum:

- [ ] `Build and test`;
- [ ] `Container smoke test`, including the Production-shaped pass;
- [ ] `Release upgrade test`;
- [ ] CodeQL analysis for C# and GitHub Actions;
- [ ] documentation build/validation;
- [ ] dependency review for the pull request that selected the release commit;
- [ ] OWASP dependency check;
- [ ] actionlint and zizmor workflow analysis; and
- [ ] any additional checks required by the repository ruleset at release time.

A green run from another commit does not satisfy this gate.

### Security gate

There must be no unresolved Critical or High security finding applicable to the release. Check GitHub code scanning,
Dependabot, secret scanning, dependency review, OWASP output, and current OpenSSF Scorecard results.

A time-based or tool-limitation result is not silently "fixed." For example, a repository-age score that cannot pass
until the repository is old enough is recorded as a limitation with the tool result and date.

A security exception requires a versioned Markdown record committed before the release under
`docs/release-exceptions/`. It must identify the finding, severity, affected surface, compensating control, owner,
review date, expiration/revisit date, and why shipping is preferable to delaying. Critical/High exceptions require an
explicit maintainer decision in the release pull request. An expired exception blocks release.

### Documentation and release metadata gate

- [ ] Walkthrough and generated API documentation describe the candidate commit.
- [ ] `CHANGELOG.md` contains a heading for the exact stable version and release date.
- [ ] `Directory.Build.props` has the exact stable `VersionPrefix` and an empty `VersionSuffix`.
- [ ] README version/release statements agree with the candidate.
- [ ] `scripts/Validate-StableRelease.ps1 -RequireStable -Tag vX.Y.Z` succeeds against the tagged commit.
- [ ] The tag is annotated, follows `vMAJOR.MINOR.PATCH`, and resolves to the exact release commit.
- [ ] GitHub release notes use the same version and changelog scope.

### Release-candidate / soak gate

The first stable release uses a minimum **7-day release-candidate soak** after the candidate commit is selected. During
the soak:

- no compatibility-affecting code is merged into the candidate;
- security and dependency alerts are reviewed;
- Production-shaped container validation and the release-upgrade test remain green; and
- any change to the candidate restarts the seven-day period.

A future policy version may remove or shorten this requirement only by an explicit documented policy change; it is not
waived ad hoc for the first stable release.

## Stable release procedure

1. Complete or disposition every graduation item above.
2. Select the merged `main` commit that will become the release candidate and record its SHA.
3. Change `Directory.Build.props` to the stable version: for the first stable release, use `VersionPrefix` `1.0.0`,
   clear `VersionSuffix`, and align assembly/file versions as required by repository policy.
4. Move the release's `CHANGELOG.md` entries from `Unreleased` under `## 1.0.0 - YYYY-MM-DD`.
5. Open and merge the release PR. Do not add compatibility-affecting commits to the selected candidate during soak.
6. Record successful hosted evidence for the exact merged commit and complete the seven-day soak.
7. Create an **annotated** `v1.0.0` tag at the exact merged release commit.
8. Run:

   ```powershell
   ./scripts/Validate-StableRelease.ps1 -RequireStable -Tag v1.0.0
   ```

9. Publish the GitHub release from that tag with **pre-release disabled** and notes matching the changelog.
10. Verify the published tag, release assets/notes, documentation, container startup, `/health/ready`, and supported
    database upgrade path.

## Rollback, withdrawal, and post-release handling

Database migrations are forward-only. If deployment fails before a migration changes the database, roll back the
application deployment normally. If a migration has changed the database, restore the pre-upgrade database and Data
Protection key-ring backup before running older binaries.

If a published release is found unsafe:

1. mark the GitHub release as withdrawn/deprecated in its notes;
2. publish a correcting release as soon as practical;
3. do not move or rewrite the existing tag;
4. document whether operators must restore data or may upgrade forward; and
5. record the incident/finding in the changelog and security process as appropriate.

## Mechanical validation

`scripts/Validate-StableRelease.ps1` intentionally separates repository metadata checks from hosted-security evidence.

Without `-RequireStable`, CI and developers can run it during the alpha series to validate that the policy, README link,
version metadata, and changelog structure remain coherent.

With `-RequireStable`, it additionally requires:

- stable SemVer with no prerelease suffix;
- major version 1 or greater;
- a matching dated changelog heading;
- a clean working tree;
- an annotated `vMAJOR.MINOR.PATCH` tag; and
- that the tag resolves to `HEAD`.

Hosted checks, security alerts, and exception approvals are recorded as URLs/records in the graduation checklist because
their state is remote, permission-sensitive, and can change independently of repository files. That is the named
exception to local mechanical validation; the evidence remains mandatory.
