# AsiBackbone Reference Application

The AsiBackbone Reference Application is an ASP.NET Core host built from the [.NET Core Application Template (NCAT)](https://github.com/AsiBackbone/NetCoreApplicationTemplate). It integrates [AsiBackbone](https://github.com/AsiBackbone/AsiBackbone) step by step: the application provides runnable sample governance rules that can be read, tested, and adapted, persists the resulting decision evidence alongside NCAT's mutation audit, and makes that evidence observable and its host-owned overhead measurable.

- **Documentation:** <https://asibackbone.github.io/ReferenceApplication/> (API reference and integration walkthrough)
- **Training:** [AsiBackbone Learning](https://asibackbone.github.io/Learning/) is the authoritative training site.

> [!NOTE]
> The Reference Application is an example, not a product. Its sample governance rules illustrate patterns and are not recommendations for any specific domain.

## Status

The eight-step [Integration Walkthrough](https://asibackbone.github.io/ReferenceApplication/walkthrough/) is complete,
including an operational evidence explorer, a Development-only governance sandbox, and governance-overhead benchmarks:

| Step | Status |
|:---|:---|
| 1. Scaffold from NCAT | Complete |
| 2. Add AsiBackbone packages | Complete |
| 3. Register AsiBackbone services | Complete |
| 4. Define governance rules | Complete |
| 5. Govern endpoints | Complete |
| 6. Persist and verify audit evidence | Complete |
| 7. Observe governance in action | Complete |
| 8. Benchmark governance overhead | Complete |

## Branches

| Branch | Purpose |
|:---|:---|
| `main` | Default ongoing development and integration branch; GitHub Pages deploys accepted changes from here |
| `ncat-baseline` | The unmodified NCAT scaffold, protected, kept for comparison |

These are the only permanent branches. Make each change on a short-lived working branch, named for the task, and open a pull request into `main`, or into `ncat-baseline` for a raw NCAT refresh.

To see everything the Reference Application adds on top of NCAT:

```powershell
git diff ncat-baseline main
```

The desired branch rulesets for `main` and `ncat-baseline` are versioned under `eng/repository-controls/`.

## Development and releases

`main` remains open for ongoing integration through pull requests. A stable commit is marked with a semantic-version tag such as `v1.0.0` and published as a GitHub release; development then continues on `main` toward the next release.

The release version is set by `VersionPrefix` and `VersionSuffix` in `Directory.Build.props` and matches the tag, for example `0.1.0` and `alpha.1` for `v0.1.0-alpha.1`. Completing the walkthrough does not by itself declare a stable public contract; releases remain `0.x` pre-releases until the repository's release-readiness criteria are met. The release version is independent of the NCAT version, which the `ncat-v<version>` tags record.

The `ncat-baseline` branch is updated separately through a pull request containing only the raw scaffold generated from the selected NCAT version. Bringing a new baseline into the Reference Application is a second, deliberate integration change targeting `main`.

## Repository layout

| Path | Contents |
|:---|:---|
| `src/ReferenceApplication.Web` | ASP.NET Core host: startup, middleware pipeline, authentication, controllers, Razor Pages, health checks |
| `src/ReferenceApplication.Infrastructure` | EF Core `ApplicationDbContext`, save-changes pipeline, and NCAT mutation audit |
| `tests/ReferenceApplication.Web.Tests` | Automated tests |
| `tests/ReferenceApplication.Benchmarks` | On-demand host governance overhead benchmarks |
| `docs/` | DocFX documentation site source |
| `scripts/` | Documentation validation scripts |
| `eng/repository-controls/` | Recorded GitHub branch rulesets |

## How it was generated

The application was generated from NCAT 2.11.4 with the default template options:

```powershell
dotnet new netcoreapp-template --name ReferenceApplication
```

Those defaults select:

- cookie authentication with authenticated-by-default routed endpoints; and
- SQLite for local development data access.

For other template options, see [NCAT Template Packaging](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/template-packaging.html).

The current NCAT baseline is **2.11.5**. The initial 2.11.4 scaffold was later refreshed to 2.11.5 (#1 on
`ncat-baseline`, carried into `main` by #2), using the same name and options. The `ncat-baseline` branch holds the
unmodified 2.11.5 scaffold, tagged `ncat-v2.11.5`; `ncat-v2.11.4` marks the original generation. To adopt a later
NCAT release, follow
[Refresh the NCAT Baseline](https://asibackbone.github.io/ReferenceApplication/walkthrough/refresh-ncat-baseline.html).

## Prerequisites

- .NET SDK 10.0 or later.
- Docker Desktop or a compatible container runtime, only if you plan to use Docker Compose.

`global.json` pins SDK selection for local development and CI. It also selects Microsoft.Testing.Platform for `dotnet test`. The test project uses `xunit.v3`.

## Restore, build, and test

```powershell
dotnet restore --locked-mode
dotnet build --configuration Release
dotnet test --configuration Release
```

To collect code coverage the same way CI does, with the test project's `coverlet.MTP` extension, and summarize it:

```powershell
dotnet test --configuration Release --results-directory TestResults/coverage --coverlet --coverlet-output-format cobertura --coverlet-exclude-by-file "**/Migrations/*.cs"
./scripts/Write-CoverageSummary.ps1 -ReportDirectory TestResults/coverage
```

CI reports coverage on every run without a threshold gate. It adds the summary to the job and publishes the Cobertura
report as the `coverage-report` artifact. Generated EF Core migrations and the test assembly are excluded. Remove
`TestResults/coverage` before running the command again, because the summary expects a single report.

Each project has a checked-in `packages.lock.json`. Locked mode restores the exact recorded dependency graph and fails if the projects and lock files disagree. After an intentional dependency change, run `dotnet restore --force-evaluate`, review the lock-file changes, and commit them with the change.

## Run locally

```powershell
dotnet run --project src/ReferenceApplication.Web
```

The local profile may pick an available HTTPS or HTTP port, depending on launch settings.

## Run with Docker Compose

```powershell
docker compose up --build
```

The application is then available at `http://localhost:8080`, with health endpoints at `/health`, `/health/ready`, and `/health/live`.

Compose runs two services. They share one configuration, so migrations always target the database the application uses:

| Service | Role |
|:---|:---|
| `referenceapplication.migrations` | One-shot. Built from the Dockerfile's `migrations` target, an EF Core migration bundle applies the checked-in migrations to `ApplicationDatabase` on the `referenceapplication-data` volume, then exits. Running it again applies only pending migrations. |
| `referenceapplication.web` | Starts only after the migrations service completes successfully. The application itself still never migrates `ApplicationDatabase` at startup. |

Compose defaults to `Development` with the governance explorer's demo identity enabled. In that mode the web service
uses the disposable `DemonstrationDatabase`, which it migrates and resets itself and which Compose also keeps on the
data volume. Set `REFERENCEAPPLICATION_DEMOIDENTITY_ENABLED=false` to serve `ApplicationDatabase` instead. To apply
migrations again without restarting the application:

```powershell
docker compose run --rm referenceapplication.migrations
```

`docker compose down --volumes` deletes both databases, the Data Protection key ring, and the logs.

Optional local overrides go in a `.env` file copied from `.env.example`:

```powershell
Copy-Item .env.example .env
```

Do not commit `.env` files.

## Configuration and security notes

The NCAT baseline behavior is documented authoritatively on the [NCAT documentation site](https://asibackbone.github.io/NetCoreApplicationTemplate/). The points below matter most when running this host.

### Endpoint access

Every routed endpoint requires an authenticated user unless it is deliberately marked anonymous. The intentionally anonymous endpoints are:

- `GET /Account/Login` and `GET /External/Challenge`, to enter the authentication flow;
- `GET /Account/AccessDenied` and `GET /Home/Error/{statusCode?}`, so failure handling cannot redirect recursively; and
- `/health`, `/health/live`, and `/health/ready`, for infrastructure probes;
- `GET /` is explicitly anonymous as the public project entry point; the explorer and its governed operations retain
  their documented authentication and authorization boundaries; and
- `GET /api/v1/governed-operations` is the intentionally ungoverned, read-only description of the Step 5 sample.

The Step 7 evidence query at `GET /api/v1/governance-evidence/{correlationId}` requires the configured administrator role. Its response is intentionally minimized: it exposes identifiers, outcomes, reason codes, policy and evidence hashes, counts, statuses, and timestamps, but never NCAT mutation values or request bodies.

The Step 7 Server-Sent Events stream at `GET /api/v1/governance-evidence/notifications` is a best-effort freshness
hint, not an authoritative event log. The built-in `InMemoryGovernanceEvidenceNotifier` is process-local: each
subscriber buffers at most 32 hints and drops the oldest first when full. Persisted evidence remains the source of
truth, and the explorer re-queries it after each hint.

For multiple replicas, sticky sessions can keep the explorer and the governed requests it initiates on one replica.
If evidence can be written by other replicas, place a shared backplane (for example, Redis pub/sub) behind
`IGovernanceEvidenceNotifier` so freshness hints can cross process boundaries. Sticky sessions alone do not relay
notifications produced by another process. A missed hint never invalidates the ledger; the next re-query returns the
authoritative persisted timeline.

`POST /Account/Logout` and all Step 5 governed operations require an authenticated user and a valid antiforgery token. Restrict production access to the health endpoints at the ingress, firewall, or proxy layer.

### Governance sandbox (Development only)

The explorer also offers a local [governance sandbox](https://asibackbone.github.io/ReferenceApplication/walkthrough/07-observe-governance.html#governance-sandbox) for experimenting with the governance mechanics: simulating and replaying decisions, following background delivery, and triggering failure paths. It is a learning aid, **not a production feature**. Each tool has its own switch, is registered only when the environment is exactly `Development`, is administrator-only with antiforgery protection on every state-changing request, and never edits or deletes persisted evidence. Tests prove every tool is absent in Staging and Production.

In Development, `ReferenceApplication:GovernanceLoadDemonstration:Enabled` adds an administrator-only bounded run to the explorer. It evaluates the production policy composition in-process, permits only one cancellable run at a time, and enforces both iteration and duration ceilings. The streamed throughput and latency histogram are transient demonstration feedback—not benchmark results—and the run persists no synthetic evidence. Use the [published BenchmarkDotNet results](https://asibackbone.github.io/ReferenceApplication/benchmarks/governance-overhead.html) for repeatable measurements.

In Development, `ReferenceApplication:PolicySimulator:Enabled` adds an administrator-only policy simulator to the explorer. It runs AsiBackbone's endpoint governance pipeline against a simulated request for a sample operation and shows each constraint, the decision-policy step, the capability-grant check, and the receipt that would be appended. It persists nothing and never shows signing key material.

In Development, `ReferenceApplication:DecisionReplay:Enabled` adds an administrator-only decision replay to the explorer. It finds a persisted decision by receipt identifier or from the recent decisions, shows which evaluation inputs the minimized ledger records and which it can only infer, and replays the decision against the current policy and an optional what-if candidate, side by side. A replay never writes to the ledger, and a policy-hash mismatch is flagged before replay.

In Development, `ReferenceApplication:PipelineInspector:Enabled` adds an administrator-only pipeline inspector to the explorer. It times one governed request's stages from timestamps the pipeline already stores, marking where the response ends and background delivery begins, and shows the audit-completion outbox, reconciliation findings, and in-process telemetry. Its timings are demonstration, not benchmark results; it adds nothing to the governed request path and never writes. While active, it also registers NCAT audit reconciliation for Development.

In Development, with the demo identity's demonstration database in use, `ReferenceApplication:FailureScenarios:Enabled` adds administrator-only failure scenarios to the explorer. Eight presets trigger failure and attack paths against the real governed endpoints: a missing capability grant, a throwing constraint, a failed ledger append, a failed outbox delivery, an execution failure after approval, evidence tampering, and, with signed tokens enabled, payload tampering and an expired token. Each shows the HTTP outcome, the decision and reason codes, and the evidence left behind. Faults are injected only into the one request a run is armed for, and nothing is registered outside Development.

### Data Protection keys

Keys default to `DataProtection-Keys` under the content root. Keep this directory durable and access-restricted. All replicas must share the key ring and use the same `ReferenceApplication:DataProtection:ApplicationName`. Changing either one invalidates existing authentication cookies and antiforgery tokens. Docker Compose supplies a persistent named volume for the key ring.

### Forwarded headers and rate limiting

Outside Development, startup fails when forwarded client-IP processing and rate limiting are both enabled without a `KnownProxies` or `KnownNetworks` trust boundary. Configure the deployment's proxy addresses or ingress CIDR, or disable forwarded headers when clients reach the application directly.

### Data access

The host uses SQLite through `ConnectionStrings:ApplicationDatabase` and keeps NCAT's checked-in SQLite migration history. Normal application databases never migrate automatically at startup; apply migrations deliberately during setup or deployment. The disposable Development governance demonstration is the sole exception: while its guarded demo identity is active, it selects `ConnectionStrings:DemonstrationDatabase`, applies migrations to that separate SQLite file, and exposes an administrator-only reset control. Walkthrough Step 6 added AsiBackbone's model configuration and a host-owned migration to `ApplicationDbContext`; the host retains ownership of the provider, connection strings, and migrations.

## Documentation site

The DocFX site under `docs/` is published to GitHub Pages by `.github/workflows/publish-docs.yml` on pushes to `main`. Pull requests that touch the docs build and validate the site but do not publish it. The site covers only the generated API reference and the walkthrough of how NCAT and AsiBackbone were combined.

Build and preview it locally:

```powershell
dotnet tool restore
dotnet build ReferenceApplication.slnx --configuration Release
dotnet tool run docfx -- docs/docfx.json --warningsAsErrors
./scripts/Validate-LandingPage.ps1 -SiteDirectory ./docs/_site
dotnet tool run docfx -- serve docs/_site
```

## Related projects

| Project | Role |
|:---|:---|
| [NetCoreApplicationTemplate](https://github.com/AsiBackbone/NetCoreApplicationTemplate) | Secure-by-default ASP.NET Core template that generated this host |
| [AsiBackbone](https://github.com/AsiBackbone/AsiBackbone) | Governance and policy-control framework this host adopts |
| [Learning](https://github.com/AsiBackbone/Learning) | The authoritative training site for the architecture and patterns |

Neither project depends on the Reference Application.

## License

This project is licensed under the MIT License; see `LICENSE.txt`. Third-party asset and notice information is in `ASSETS-LICENSES.md`.
