---
title: "Supplemental: Create the Development Database"
description: How to apply the inherited NCAT migration history and create the local SQLite database explicitly.
---

# Supplemental: Create the Development Database

**Status:** Available after Step 3 for local development.

This checkpoint creates the host-owned SQLite development database from the inherited NCAT Entity Framework Core
migration history. It is supplemental rather than a numbered AsiBackbone integration step because the database provider,
connection string, migrations, and deployment procedure belong to the Reference Application host.

## Why this is explicit

The default connection string is `Data Source=application-dev.db`, and the default data-access provider is SQLite. The
application deliberately does not call `EnsureCreated`, `Migrate`, or their asynchronous equivalents during startup.
This prevents an application instance from changing a deployed database implicitly and keeps migration execution under
operator control.

The disposable governance demonstration described in
[Step 7](07-observe-governance.md#disposable-development-database) is the only exception. When its guarded
Development identity is active, it selects and migrates a separate `DemonstrationDatabase`; it never migrates or resets
the normal `ApplicationDatabase` described on this page.

Until the migrations are applied, the SQLite file does not exist and the database readiness check reports the application
as unavailable. Running the web host alone does not create the file.

## Apply the inherited migrations

The repository already contains the NCAT migration history under
`src/ReferenceApplication.Infrastructure/Data/Migrations`. Restore the repository's pinned Entity Framework Core command-line
tool, then run the update with the web project as the working directory:

```powershell
dotnet tool restore

Push-Location src/ReferenceApplication.Web

dotnet tool run dotnet-ef migrations list `
  --project ../ReferenceApplication.Infrastructure `
  --startup-project . `
  --context ApplicationDbContext

dotnet tool run dotnet-ef database update `
  --project ../ReferenceApplication.Infrastructure `
  --startup-project . `
  --context ApplicationDbContext

Pop-Location
```

The EF tools stop the temporary web host after resolving its design-time services. Since walkthrough Step 3, the startup
`catch` excludes the `HostAbortedException` used for that stop, so no fatal error is logged. The command succeeded when it
exits with code `0` and prints the migration list or `Done.`

Using the web project as the working directory makes the relative SQLite path deterministic. The command creates:

```text
src/ReferenceApplication.Web/application-dev.db
```

The database and its `-shm` and `-wal` sidecar files are development artifacts covered by the repository's `.gitignore`;
do not commit them.

## Verify the result

From the repository root, confirm that the file exists:

```powershell
Test-Path src/ReferenceApplication.Web/application-dev.db
```

After starting the application, `/health/ready` should report healthy when the database is reachable and the other
readiness dependencies are available.

## In Docker Compose

The runtime image contains no SDK or EF tooling, so the commands above cannot run inside it. Docker Compose provides
the same explicit step through a one-shot `referenceapplication.migrations` service instead:

- The Dockerfile's `migrations` target packages the checked-in migrations as an EF Core migration bundle.
- The service applies the bundle to `ApplicationDatabase` on the `referenceapplication-data` volume, then exits.
- The web service starts only after that service completes successfully.
- The application still never migrates `ApplicationDatabase` at startup.

To apply migrations again, for example after pulling a new migration:

```powershell
docker compose build referenceapplication.migrations
docker compose run --rm referenceapplication.migrations
```

## Relationship to Step 6

The repository's migration history also includes the Step 6 migration, `AddGovernedAuditEvidence`, which adds the
AsiBackbone persistence tables to the host-owned `ApplicationDbContext`. The `database update` command above applies the
whole history, including that migration and `AddCapabilityGrantUses`, which adds the
[signed capability token](signed-capability-tokens.md) replay table. If you created the database before either, run the
same command again; it applies only the pending migrations. Migrations never run automatically at application startup.

The repository already contains every migration, so do not run `migrations add` here. See
[Step 6: Persist and Verify Audit Evidence](06-persist-and-verify-audit.md#how-the-migration-was-created) for how the
Step 6 migration was created.

## Authoritative references

- [NCAT Data Access](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/data-access.html)
- [AsiBackbone EF Core Host Ownership and Migrations](https://asibackbone.github.io/AsiBackbone/articles/ef-core-host-ownership-and-migrations.html)

## Next

[Step 4: Define governance rules](04-define-governance-rules.md)
