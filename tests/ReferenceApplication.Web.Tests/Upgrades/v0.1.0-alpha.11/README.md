# Upgrade boundary: v0.1.0-alpha.11

The oldest release a database upgrade is supported from. `ReleaseUpgradeTests` and the CI **Release upgrade test**
job start from here.

| File | What it is |
| --- | --- |
| `evidence.sql` | Representative evidence captured from this release through its own application paths, as SQL inserts. Load it into a database migrated to `20261021151500_AddCapabilityGrantUses`, this release's last migration. |
| `migrations.sha256` | The SHA-256 of every migration file this release shipped, with line endings normalized to LF. Shipped migrations are immutable; a schema change needs a new migration. |

## What the fixture holds

`ReleaseUpgradeEvidence.ProduceAsync` creates it through the real endpoints, dispatcher, and reconciler:

- an allowed and a denied decision, each with its hashed ledger record, metadata, and reason codes;
- two governed records with NCAT mutation audit;
- an audit completion delivered by the outbox, with its AsiBackbone lifecycle event;
- reconciliation findings, including one for a record whose completion was never staged; and
- a signed capability token issued and used once, so a decision carries the token identifier and a capability-grant
  use record exists.

Two of the three findings are for the outbox's own entry, which the NCAT audit records and reconciliation reports as
missing a completion. That is how this release behaves, so the fixture keeps it.

## Regenerate

Regenerate only when choosing a new boundary, on the release that becomes the boundary, whose last migration must be
the boundary migration:

```powershell
$env:REFERENCEAPPLICATION_GENERATE_UPGRADE_FIXTURE = "$PWD/tests/ReferenceApplication.Web.Tests/Upgrades/<release>/evidence.sql"
dotnet test ReferenceApplication.slnx -c Release --filter "FullyQualifiedName~GenerateBoundaryFixture"
```

Then update `ReleaseUpgradeEvidence.BoundaryRelease` and `BoundaryMigration`, write the new `migrations.sha256`, and set
`PREVIOUS_RELEASE` and `FIXTURE` in the CI **Release upgrade test** job. `ReleaseUpgradeTests` checks that these agree.
