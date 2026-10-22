---
title: "Supplemental: Run Outside Development"
description: The production deployment checklist and the production-shaped reference configuration that CI starts on every pull request.
---

# Supplemental: Run Outside Development

**Status:** Available after Step 8. A production-shaped reference configuration is checked in as
`docker-compose.production.yml`, and CI starts it in `Production` on every pull request
([#72](https://github.com/AsiBackbone/ReferenceApplication/issues/72)).

The rest of the walkthrough runs in `Development`, where the demo identity and the governance sandbox make the
governance path easy to watch. This page collects, in one place, every decision a deployment outside Development has
to make. It is a reference configuration that proves the documented boundaries compose. It is **not** a hosting
recommendation, and the Reference Application is not a deployable product.

## What changes outside Development

Outside `Development`, the host registers none of the following, whatever their switches say:

- the demo identity and its sign-in route, and the disposable demonstration database;
- the governance sandbox: load demonstration, policy simulator, decision replay, pipeline inspector, and failure
  scenarios.

At startup the host logs `Security posture: no Development-only components are registered.` This line comes from the
running host's own service registrations, not from its configuration. When any Development-only component is
registered, it logs a warning naming each one instead.

## Checklist

| Decision | What to configure | Reference configuration |
| --- | --- | --- |
| Environment | `ASPNETCORE_ENVIRONMENT=Production` | Set for both the migrations and web services |
| Host names | `AllowedHosts`, the public host names, separated by semicolons. Health probes must send one of them as the `Host` header. | Required: `REFERENCEAPPLICATION_ALLOWEDHOSTS` |
| TLS and proxy trust | Terminate TLS at the ingress proxy. Trust forwarded client address and scheme only from it: `ReferenceApplication:ForwardedHeaders:KnownProxies` or `KnownNetworks`, with `ClearKnownNetworksAndProxies` false. Startup fails outside Development when forwarded headers and rate limiting are enabled without a trust boundary. | Required: `REFERENCEAPPLICATION_FORWARDEDHEADERS_KNOWNNETWORK`, the ingress network's CIDR |
| Sign-in | A real external identity provider. No provider is configured by default, so without one nobody can sign in. See the provider settings under `ReferenceApplication:Authentication:Providers`. | OpenID Connect. Required: `REFERENCEAPPLICATION_OIDC_AUTHORITY`, `REFERENCEAPPLICATION_OIDC_CLIENTID`, `REFERENCEAPPLICATION_OIDC_CLIENTSECRET` |
| Secrets | Inject secrets from the platform's secret store as environment variables or files. Never put them in `appsettings` or source. | Every secret is required from the environment, and the certificate is a Compose file secret. Compose refuses to start when one is missing. |
| Data Protection | `ReferenceApplication:DataProtection:KeyRingPath`, an absolute path on durable storage shared by every replica. `KeyEncryptionCertificatePath` and its password, so keys are encrypted at rest. Every replica uses the same `ApplicationName`. | Key ring on the `referenceapplication-data-protection-keys` volume. Certificate injected at `/run/secrets/dataprotection-certificate`. |
| Database and migrations | `ConnectionStrings:ApplicationDatabase`. Apply migrations deliberately before the application starts, against the same database; the application never migrates at startup. SQLite is the only supported provider; any other fails at startup ([Supported database provider](local-database-setup.md#supported-database-provider)). | The one-shot `referenceapplication.migrations` service runs the EF Core migration bundle with the same configuration, then the web service starts |
| Health probes | `/health/live` for liveness, `/health/ready` for readiness. All health endpoints are anonymous, and the host logs a warning saying so in Production. Restrict their external reachability at the ingress, firewall, or service mesh. | Published on the loopback interface only |
| Logs | Serilog writes to standard output for the platform to collect, and to rolling files under `/app/Logs`. Keep `IncludeQueryString`, `IncludeUserName`, and `IncludeRemoteIpAddress` off unless a reviewed need requires them. | Console output; files on the `referenceapplication-logs` volume |
| Telemetry | `ReferenceApplication:OpenTelemetry:Otlp:Enabled` and `Endpoint` to export traces and metrics over OTLP | Optional: `REFERENCEAPPLICATION_OTLP_ENABLED`, `REFERENCEAPPLICATION_OTLP_ENDPOINT` |
| Evidence notifications | The built-in notifier is process-local. With several replicas, use sticky sessions or a shared backplane behind `IGovernanceEvidenceNotifier`. The ledger stays authoritative either way. | A single replica |
| Capability tokens | Leave `ReferenceApplication:CapabilityTokens:Enabled` false, or sign through a managed key service as [Wire it for production](signed-capability-tokens.md#wire-it-for-production) describes. `AllowSampleSigningKeyInProduction` is an explicit, reviewed escape hatch, not the intended path. | Disabled |

## Run the reference configuration

Put the required values in an untracked file, for example `.env.production`, and never commit it:

```text
REFERENCEAPPLICATION_ALLOWEDHOSTS=app.example.com
REFERENCEAPPLICATION_FORWARDEDHEADERS_KNOWNNETWORK=172.31.250.0/24
REFERENCEAPPLICATION_DATAPROTECTION_CERTIFICATE_FILE=/secure/path/dataprotection.pfx
REFERENCEAPPLICATION_DATAPROTECTION_CERTIFICATE_PASSWORD=<from your secret store>
REFERENCEAPPLICATION_OIDC_AUTHORITY=https://login.example.com/
REFERENCEAPPLICATION_OIDC_CLIENTID=<client id>
REFERENCEAPPLICATION_OIDC_CLIENTSECRET=<from your secret store>
```

Then apply the production file on top of the base file:

```powershell
docker compose --env-file .env.production -f docker-compose.yml -f docker-compose.production.yml up --build
```

The migrations service applies the schema to `ApplicationDatabase`, then the web service starts in `Production` on the
`ingress` network. That network's subnet, `172.31.250.0/24` by default, is the trusted proxy range; change it with
`REFERENCEAPPLICATION_INGRESS_SUBNET` and keep `REFERENCEAPPLICATION_FORWARDEDHEADERS_KNOWNNETWORK` in step. Place your
ingress proxy on that network. Without one, the web service is still published on `127.0.0.1:8080`, which is how CI
reaches it.

## What CI proves

After the Development passes, the **Container smoke test** job generates throwaway secrets in the runner's temporary
directory: a self-signed key-encryption certificate, its password, and an OpenID Connect client secret. It then starts
the reference configuration and checks that:

- the migrations service exits successfully and `/health/ready` succeeds;
- the host runs in `Production` and logs that no Development-only component is registered, and that the demo identity
  is not active;
- none of the relaxed-posture warnings appears: permissive `AllowedHosts`, a key ring under the content root, or
  unencrypted keys;
- a request with a `Host` header outside `AllowedHosts` is rejected with 400;
- the sign-in page offers OpenID Connect and no demo sign-in; and
- the key written to the durable key ring is encrypted with the injected certificate.

The identity provider is never contacted: CI attempts no sign-in, and the handler fetches the provider's metadata only
on a challenge. The secrets are removed when the job ends.

`ProductionDeploymentTests` run the same settings in-process on every test run. They check readiness, the provider on
the sign-in page, host filtering, and the encrypted key ring. They also check what a container probe cannot see: no
Development-only service is registered and no Development-only route is mapped. `ContainerConfigurationTests` check that
`docker-compose.production.yml` keeps every Development-only switch off and takes every secret from the environment.

An outside probe cannot show that a route is absent. The fallback authorization policy sends every anonymous request,
for a mapped route or not, to the sign-in page. That is why CI relies on the startup posture line and these tests rather
than on HTTP status codes.

## What it deliberately does not cover

- **A hosting platform.** There is no Kubernetes manifest, cloud template, or ingress proxy configuration. The checklist
  says what each one must provide.
- **A real identity provider or key service.** Both are the deployment's own. The reference configuration requires them
  and never ships a value for them.
- **Database providers other than SQLite.** SQLite is the only supported provider, and the application refuses any
  other at startup. [Supported database provider](local-database-setup.md#supported-database-provider) lists what an
  adopter must add to run on another one.
- **Upgrades between releases.** [#73](https://github.com/AsiBackbone/ReferenceApplication/issues/73) covers
  release-to-release database upgrades and recovery.
