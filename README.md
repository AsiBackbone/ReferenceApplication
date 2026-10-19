# AsiBackbone Reference Application

The AsiBackbone Reference Application is an ASP.NET Core host built from the [.NET Core Application Template (NCAT)](https://github.com/AsiBackbone/NetCoreApplicationTemplate). It integrates [AsiBackbone](https://github.com/AsiBackbone/AsiBackbone) step by step: the application provides runnable sample governance rules that can be read, tested, and adapted, persists the resulting decision evidence alongside NCAT's mutation audit, and is expanding to make that evidence observable and measurable.

- **Documentation:** <https://asibackbone.github.io/ReferenceApplication/> (API reference and integration walkthrough)
- **Training:** [AsiBackbone Learning](https://asibackbone.github.io/Learning/) is the authoritative training site.

> [!NOTE]
> The Reference Application is an example, not a product. Its sample governance rules illustrate patterns and are not recommendations for any specific domain.

## Status

The original six-step [Integration Walkthrough](https://asibackbone.github.io/ReferenceApplication/walkthrough/) foundation is complete. The walkthrough is expanding with an operational evidence explorer and governance-overhead benchmarks:

| Step | Status |
|:---|:---|
| 1. Scaffold from NCAT | Complete |
| 2. Add AsiBackbone packages | Complete |
| 3. Register AsiBackbone services | Complete |
| 4. Define governance rules | Complete |
| 5. Govern endpoints | Complete |
| 6. Persist and verify audit evidence | Complete |
| 7. Observe governance in action | In progress |
| 8. Benchmark governance overhead | Planned |

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

The release version is set by `VersionPrefix` and `VersionSuffix` in `Directory.Build.props` and matches the tag, for example `0.1.0` and `alpha.1` for `v0.1.0-alpha.1`. While the expanded integration walkthrough remains in progress, releases are `0.x` pre-releases. The release version is independent of the NCAT version, which the `ncat-v<version>` tags record.

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
- `GET /` is explicitly anonymous for the current walkthrough stage; authentication requirements for the landing page will be revisited as integration progresses; and
- `GET /api/v1/governed-operations` is the intentionally ungoverned, read-only description of the Step 5 sample.

The Step 7 evidence query at `GET /api/v1/governance-evidence/{correlationId}` requires the configured administrator role. Its response is intentionally minimized: it exposes identifiers, outcomes, reason codes, policy and evidence hashes, counts, statuses, and timestamps, but never NCAT mutation values or request bodies.

`POST /Account/Logout` and all Step 5 governed operations require an authenticated user and a valid antiforgery token. Restrict production access to the health endpoints at the ingress, firewall, or proxy layer.

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
