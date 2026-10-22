---
title: "Supplemental: Upgrade Between Releases"
description: The supported database upgrade boundary, how upgrades are tested, and the backup, recovery, and rollback rules for the governance evidence database.
---

# Supplemental: Upgrade Between Releases

**Status:** Available from `v0.1.0-alpha.11`. Every pull request upgrades a database created by that release, and
checks that no evidence was lost or changed ([#73](https://github.com/AsiBackbone/ReferenceApplication/issues/73)).

The application database holds governance evidence: decision receipts and their hashed ledger records, NCAT mutation
audit and manifests, audit completions and lifecycle events, reconciliation findings, and capability-grant use
records. An upgrade must keep every row of it, unchanged and still verifiable. Creating a schema from scratch proves
none of that, so upgrades are tested from a real earlier release.

## Supported upgrade boundary

| From | To | Supported |
| --- | --- | --- |
| `v0.1.0-alpha.11` or later | Any later release | Yes. Tested on every pull request. |
| Earlier than `v0.1.0-alpha.11` | Any release | No. Those pre-releases predate the tested boundary; start from a new database. |

The supported provider is SQLite ([#74](https://github.com/AsiBackbone/ReferenceApplication/issues/74) decides any other).
The boundary may move forward when a release is declared stable; it never moves silently.

## How upgrades are tested

**Fixture.** The boundary release's representative evidence is captured, through its own application paths, as
`tests/ReferenceApplication.Web.Tests/Upgrades/v0.1.0-alpha.11/evidence.sql`. Its
[README](https://github.com/AsiBackbone/ReferenceApplication/tree/main/tests/ReferenceApplication.Web.Tests/Upgrades/v0.1.0-alpha.11)
lists what it holds and how to regenerate it for a new boundary.

**`ReleaseUpgradeTests`**, on every test run:

- **Shipped migrations are immutable.** Every migration file the boundary release shipped still hashes to the value in
  `migrations.sha256`, and no migration has been inserted before the boundary.
- **No row is lost or changed.** A database is migrated to the boundary's last migration, loaded with the fixture,
  and upgraded with the current migrations. Every table, column, row, and value that existed before still exists,
  unchanged; new tables, columns, and rows are allowed.
- **Upgrades are idempotent.** A second upgrade changes nothing.
- **Upgrade and clean install agree.** The upgraded schema is the same as a clean install's.
- **The evidence still works.** The upgraded application becomes ready, and its own services check that:
  - every decision record still verifies against its stored hash;
  - mutation audit and audit completions still link to their decisions;
  - delivered completions still have their lifecycle events;
  - a delivered request's timeline still has its decision, mutation-audit, completion, and lifecycle evidence;
  - the token-authorized decision still has its use record; and
  - reconciliation finds nothing new.
- **The checks catch real damage.** Stand-ins for destructive migrations are each reported: deleted lifecycle events,
  a changed decision field that breaks its hash, a dropped column, and deleted capability-grant uses.

**The CI Release upgrade test job** repeats the upgrade with real release artifacts:

1. It builds the boundary release's migration bundle from its tag and creates a database with it.
2. It seeds the database with the fixture.
3. It upgrades the database twice with this commit's bundle and checks that the second upgrade changes nothing.
4. It starts this commit's application on the upgraded database and waits for `/health/ready`.
5. It runs the deep checks above against that database.

## Before upgrading: back up

Migrations are forward-only, and nothing reverses them for you. Back up before every upgrade:

1. **Stop the application,** or at least stop writes, so the database is consistent. A running outbox worker or
   reconciliation pass also writes.
2. **Copy the database.** For SQLite, use `sqlite3 application.db ".backup 'application-<release>.db'"`, or copy the
   database file together with any `-wal` and `-shm` files while the application is stopped.
3. **Back up the Data Protection key ring** with it. Without the keys, authentication cookies and antiforgery tokens
   issued before the backup cannot be read after a restore.
4. **Check the backup** opens and holds rows, for example with `sqlite3 application-<release>.db "PRAGMA integrity_check;"`.

## Upgrade

1. Take the backup.
2. Run the new release's migration bundle against the database. In Docker Compose, that is the
   `referenceapplication.migrations` service; see [Run outside Development](run-in-production.md). The application never
   migrates at startup.
3. Start the new release and wait for `/health/ready`.
4. Optionally, verify the evidence: the explorer's **Verify hashes**, or the evidence verification API, for a few recent
   correlations.

## If an upgrade fails

The migration bundle stops at the first failure. Depending on the provider and the operations involved, the database
can be left partly migrated: earlier migrations applied, and the failing one rolled back or partly applied. Treat any
failed upgrade as leaving the database in an unknown state.

- **Do not start either release against a partly migrated database.** The old release does not know the new schema,
  and the new release expects all of it.
- **Recover from the backup:** stop everything, restore the database and key ring from the backup, and start the
  release you upgraded from. Investigate the failure before trying again.
- **Do not edit the migration history table by hand** to skip a migration. A skipped migration leaves the schema and
  the evidence it describes out of step.

## Rollback

Rolling back means restoring the backup taken before the upgrade, and accepting that evidence written since is lost.

- **Do not run an older release against an upgraded database.** Once a migration has changed the schema, the older
  binaries may fail, or worse, write evidence the newer schema reads differently.
- **Down migrations are not supported.** They exist in the generated code but are not tested. A migration that
  removes or reshapes data cannot restore it, so they are never a recovery path for evidence.
- **Evidence written after the upgrade is not carried back.** If it must be kept, export it before restoring: for
  example, the decision records and their hashes through the evidence query and verification APIs.
