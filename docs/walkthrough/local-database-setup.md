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

## Relationship to Step 6

The repository's migration history also includes the Step 6 migration, `AddGovernedAuditEvidence`, which adds the
AsiBackbone persistence tables to the host-owned `ApplicationDbContext`. The `database update` command above applies the
whole history, including that migration. If you created the database before Step 6, run the same command again; it
applies only the pending migration. Migrations never run automatically at application startup.

The repository already contains every migration, so do not run `migrations add` here. See
[Step 6: Persist and Verify Audit Evidence](06-persist-and-verify-audit.md#how-the-migration-was-created) for how the
Step 6 migration was created.

## Authoritative references

- [NCAT Data Access](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/data-access.html)
- [AsiBackbone EF Core Host Ownership and Migrations](https://asibackbone.github.io/AsiBackbone/articles/ef-core-host-ownership-and-migrations.html)

## Next

[Step 4: Define governance rules](04-define-governance-rules.md)
