# Security Policy

The AsiBackbone Reference Application is a runnable ASP.NET Core host that demonstrates how an
[NCAT](https://github.com/AsiBackbone/NetCoreApplicationTemplate)-generated application adopts
[AsiBackbone](https://github.com/AsiBackbone/AsiBackbone). It is an example, not a product.

This file adds only what is specific to this host. The
[AsiBackbone organization security policy](https://github.com/AsiBackbone/.github/blob/main/SECURITY.md) is the
default for everything else: what to include in a report, how to handle sensitive data, and what to expect after you
report. Where the two differ, this file applies to this repository.

## Report privately

Report a suspected vulnerability in this repository through
[GitHub private vulnerability reporting](https://github.com/AsiBackbone/ReferenceApplication/security/advisories/new)
(the **Security** tab, then **Report a vulnerability**). Do not open a public Issue, pull request, or Discussion with
exploit details, secrets, tokens, keys, or personal data.

Follow the organization policy for the general report contents. For this host, also say:

- the release tag or commit, and the `ASPNETCORE_ENVIRONMENT` you ran;
- whether the demo identity or any governance sandbox tool was active, which the startup `Security posture:` log line
  states;
- how you ran it: `dotnet run`, `docker-compose.yml` alone, or with `docker-compose.production.yml` layered on top; and
- any configuration you changed from the checked-in files, with secrets redacted.

Non-sensitive hardening suggestions and documentation corrections can go to a normal Issue or pull request.

## Supported versions

The repository has not yet published a stable release. Every release is a `0.x` prerelease, and the
[Stable Release Policy v1](docs/stable-release-policy-v1.md) defines what a stable release must satisfy first.

| Version line | Security support |
| --- | --- |
| `main` | Fixes land here first. Reports against the current `main` commit are welcome, but `main` is a development branch, not a supported release line. |
| Latest `0.x` prerelease | Supported. Confirmed vulnerabilities are fixed on `main` and shipped in the next prerelease. |
| Earlier `0.x` prereleases | Not supported. Nothing is backported; upgrade to the latest prerelease. Database upgrades are supported from `v0.1.0-alpha.11` onward ([Upgrade between releases](docs/walkthrough/upgrade-between-releases.md)). |
| `1.x` stable | Not yet released. The first stable release updates this table to name the supported stable line and how prereleases are handled after it. |

## Where to report

This host combines code from three repositories. Report to the one that owns the affected behavior.

| The problem is in | Report to |
| --- | --- |
| An AsiBackbone package, API, analyzer, or runtime behavior, reproducible without this host's own code | [AsiBackbone](https://github.com/AsiBackbone/AsiBackbone/security/advisories/new) |
| NCAT's generated scaffold or template defaults, reproducible in a fresh `dotnet new netcoreapp-template` scaffold with the same options | [NetCoreApplicationTemplate](https://github.com/AsiBackbone/NetCoreApplicationTemplate/security/advisories/new) |
| Anything this repository adds or changes on top of the NCAT scaffold, its configuration files, container and Compose files, workflows, or documentation | This repository |
| ASP.NET Core, EF Core, SQLite, OpenTelemetry, Serilog, or another third-party dependency | That project's own security process |

Everything this repository adds on top of the NCAT scaffold is visible with `git diff ncat-baseline main`. A finding
that depends on code or configuration in that diff belongs here.

Two cases are reported here even though the underlying fix is upstream:

- this host still references a vulnerable AsiBackbone or third-party package version, or has not adopted an NCAT
  security fix, after the fix is available; or
- this host uses an upstream component in a way its documentation warns against.

When you are unsure, report here and say so. The maintainer coordinates with the owning repository and does not need
the report filed twice.

## In scope

Generally in scope for this repository:

- a routed endpoint reachable anonymously that is not in the README's list of intentionally anonymous endpoints;
- a governed operation that performs its side effect without the decision, capability, or acknowledgment checks the
  walkthrough documents, or that accepts a replayed, expired, foreign, or mismatched grant or acknowledgment;
- an evidence response that exposes NCAT mutation values, request bodies, or signing key material, contrary to its
  documented minimization;
- a Development-only component registered, or one of its routes mapped, when the environment is not exactly
  `Development`;
- a sandbox tool that edits or deletes persisted evidence, or accepts a state-changing request without the
  administrator role and an antiforgery token;
- a checked-in configuration, Compose file, Dockerfile, or example file that contains a real credential or a weak
  default secret that is used outside Development; and
- documentation that misstates a security boundary in a way that would lead an operator to an unsafe deployment.

## Development-only facilities

The demo identity, the disposable demonstration database and its reset control, and the governance sandbox tools (load
demonstration, policy simulator, decision replay, pipeline inspector, and failure scenarios) are learning aids. Each is
registered only when the environment is exactly `Development` and its own switch is on.

Inside Development they are deliberately permissive. The demo identity lets any visitor sign in and add fixed
administrator and capability claims, the demonstration database can be reset, and failure scenarios inject faults into
real governed requests on purpose. That behavior, inside Development, is not a vulnerability. Running a Development
host where untrusted users can reach it is an operator choice outside this policy.

What is a vulnerability is any of these facilities escaping Development, as listed under [In scope](#in-scope).

## Deployment responsibilities

A deployment outside Development makes decisions this repository cannot make for it. Findings that depend only on
how a deployment made them are out of scope here. The
[production deployment checklist](docs/walkthrough/run-in-production.md) is the authoritative list.

| Responsibility | What the deployment owns |
| --- | --- |
| Authentication provider | Registering and configuring a real external identity provider, its client secret, and which users receive the administrator role and capability claims. No provider is configured by default. |
| TLS and proxy trust | Terminating TLS, setting `AllowedHosts`, and trusting forwarded headers only from the deployment's own proxy addresses or network. |
| Data Protection keys | Durable, access-restricted storage for the key ring, encryption at rest, sharing it across replicas, and backing it up with the database. |
| Database and migrations | Applying migrations deliberately before the application starts, protecting the SQLite file, and backing up before every upgrade. The application never migrates `ApplicationDatabase` at startup. |
| Health endpoints | Restricting who can reach `/health`, `/health/live`, `/health/ready`, and `/health/audit-integrity`. They are anonymous at the application layer by design. |
| Signing keys | Leaving signed capability tokens disabled, or issuing through a managed key service. The sample issuance key keeps its private key in process memory; `AllowSampleSigningKeyInProduction` is a reviewed escape hatch, not a key-custody control. |
| Development-only isolation | Running with an environment other than `Development` and checking the startup `Security posture:` line. |
| Secrets, logs, and telemetry | Injecting secrets from a secret store, keeping privacy-sensitive logging options off, and securing the log and telemetry backends. |

## What this host does not guarantee

The Reference Application demonstrates integration patterns. It does not provide certification, compliance, or an
end-to-end deployment security guarantee, and it has not been penetration tested.

- `docker-compose.production.yml` is a reference configuration that shows the documented boundaries compose. It is not
  a hosting recommendation, and CI starting it successfully is not a security assessment.
- Decision-record hash verification and audit reconciliation show how evidence can be checked. On their own they do
  not make the database immutable, externally anchored, or legally non-repudiable.
- Passing CI, CodeQL, dependency, and Scorecard checks shows that those checks passed. It does not show that the
  application is free of vulnerabilities.

## Related policies

- [AsiBackbone organization security policy](https://github.com/AsiBackbone/.github/blob/main/SECURITY.md)
- [AsiBackbone security policy](https://github.com/AsiBackbone/AsiBackbone/blob/main/SECURITY.md)
- [NetCoreApplicationTemplate security policy](https://github.com/AsiBackbone/NetCoreApplicationTemplate/blob/main/SECURITY.md)
- [Stable Release Policy v1](docs/stable-release-policy-v1.md)
