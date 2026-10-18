---
title: "Step 6: Persist and Verify Audit Evidence"
description: How governance decision evidence is planned to be persisted through the host-owned DbContext and correlated with NCAT mutation audit records.
---

# Step 6: Persist and Verify Audit Evidence

**Status:** Planned. This page will be updated with the migration, adapter code, and commit when the step lands.

## Planned outcome

Governance decision evidence will be persisted through the host-owned NCAT `ApplicationDbContext` and correlated with NCAT mutation-audit completion, so each consequential change has both a policy decision and a mutation record.

## Planned procedure

1. Apply the AsiBackbone EF Core model configuration in `ApplicationDbContext`, and generate a host-owned migration for it.
2. Register the AsiBackbone EF Core stores against the host `DbContext`.
3. Connect NCAT audit completion to AsiBackbone lifecycle evidence, following the optional adapter pattern.
4. Add tests proving that a governed request produces a decision receipt, a ledger record, and a correlated NCAT mutation-audit record.

The host continues to own the database provider, connection strings, migrations, and retention policy.

Complete [Create the development database](local-database-setup.md) first so the inherited NCAT schema is established.
When this step adds the AsiBackbone model configuration and host-owned migration, apply the updated migration history with
the same explicit `dotnet tool run dotnet-ef database update` procedure. The application will not apply the new migration
automatically at startup.

## Authoritative references

- [AsiBackbone EF Core Host Ownership and Migrations](https://asibackbone.github.io/AsiBackbone/articles/ef-core-host-ownership-and-migrations.html)
- [AsiBackbone Optional NCAT Audit-Completion Adapter](https://asibackbone.github.io/AsiBackbone/articles/ncat-audit-completion-adapter.html)
- [NCAT Audit Accountability Integration](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/audit-accountability-integration.html)
- [NCAT Data Access](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/data-access.html)
