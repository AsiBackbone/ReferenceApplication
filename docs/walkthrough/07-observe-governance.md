---
title: "Step 7: Observe governance in action"
description: Use the Development governance explorer to run the real governed endpoints and inspect their persisted evidence.
---

# Step 7: Observe governance in action

**Status:** In progress. The explorer, evidence query, live notifications, verification, and resettable demonstration
database are implemented. The bounded live-load demonstration remains tracked separately.

Step 6 established the durable evidence chain. Step 7 presents that same production-shaped governance path as an
interactive walkthrough: one panel calls the governed endpoints, and the other queries and verifies the decision,
mutation-audit, outbox, and lifecycle evidence they persist.

## Architecture and access boundary

The explorer at `/governance-explorer` does not use a demonstration governance implementation. Its controls call the
real versioned endpoints with the page's antiforgery token. The evidence panel then combines two administrator-only
interfaces:

- the governance evidence query API returns the persisted timeline for one correlation identifier; and
- the Server-Sent Events endpoint carries identifier-only freshness notifications that cause the browser to re-query
  that authoritative timeline.

Authentication is required to open the explorer. Any authenticated actor can exercise the governed endpoints and see
the resulting HTTP outcome. Full evidence, hash verification, the unfiltered notification stream, and database reset
require the configured administrator policy. The Development demo identity supplies only the two fixed claims needed
to compare the sample outcomes; it accepts no arbitrary claim values.

## Disposable Development database

Repeated walkthrough runs intentionally create immutable accountability evidence. Deleting selected ledger rows would
teach the wrong operational model, so the Reference Application does not add an evidence-deletion API. Instead, an
active Development demo selects a separate SQLite database:

```json
{
  "ConnectionStrings": {
    "DemonstrationDatabase": "Data Source=application-demo.db;Pooling=False"
  }
}
```

Before data-access services are registered, the host selects that connection string and the SQLite provider only when
the environment is exactly `Development` and `ReferenceApplication:DemoIdentity:Enabled` is `true`. It applies the
checked-in migration history to this disposable file at startup. A disabled demo—or the same switch in Staging or
Production—leaves the configured application provider and `ApplicationDatabase` connection unchanged.

Sign in as the Development demo administrator and select **Reset demonstration database** in the explorer to return to
an empty evidence state. The antiforgery-protected action deletes the whole disposable database and reapplies the real
migrations. It removes demonstration records, ledger evidence, mutation audit, outbox entries, and lifecycle events
together, without creating a selective delete path. The reset service and route are not registered outside the guarded
Development configuration, so evidence retention for normal application use is unchanged.

## Run the walkthrough

1. Start the web project in Development and open `/Account/Login`.
2. Apply the demo administrator role and, when needed, the consequential capability.
3. Open `/governance-explorer`, run the sample actions, and watch the persisted timeline update.
4. Use **Verify hashes** to compare stored decision evidence with its canonical hash.
5. Use **Reset demonstration database** when you want a fresh walkthrough state.

See [Development-only demo identity controls](development-demo-identity.md) for the fixed claims and safety boundary.
Real performance measurements are documented under [Governance benchmarks](../benchmarks/governance-overhead.md); the
interactive explorer is a demonstration, not a benchmark.

## Verification

The integration tests use temporary SQLite files and the real migration history. They prove that the Development
administrator reset clears all demonstration records and evidence, leaves a migrated empty schema, and requires an
antiforgery token. The same tests verify that both the reset service and route are absent when the switch is disabled or
the environment is Staging or Production.
