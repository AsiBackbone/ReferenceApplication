---
title: "Step 7: Observe governance in action"
description: Use the Development governance explorer to run the real governed endpoints and inspect their persisted evidence.
---

# Step 7: Observe governance in action

**Status:** Complete. The explorer combines evidence query, live notifications, verification, a resettable demonstration
database, and a bounded in-process load demonstration.

Step 6 established the durable evidence chain. Step 7 presents that same production-shaped governance path as an
interactive walkthrough: one panel calls the governed endpoints, and the other queries and verifies the decision,
mutation-audit, outbox, and lifecycle evidence they persist.

## Architecture and access boundary

The explorer at `/governance-explorer` does not use a demonstration governance implementation. Its controls call the
real versioned endpoints with the page's antiforgery token. The evidence panel then combines two administrator-only
interfaces:

- the governance evidence query API returns the persisted timeline for one correlation identifier; and
- the Server-Sent Events endpoint carries identifier-only freshness notifications that cause the browser to re-query
  that authoritative timeline. While no evidence changes, the stream writes a `: keep-alive` comment every 15 seconds
  so proxies and load balancers do not close it as idle. Each subscriber keeps at most 32 pending hints and drops the
  oldest first, so a slow browser can miss a hint but never delays a governed request; it recovers by re-querying.

These notifications are intentionally best-effort and process-local. The built-in
`InMemoryGovernanceEvidenceNotifier` does not deliver across replicas. Each subscriber buffers at most 32 hints and
drops the oldest first when full; the persisted ledger, not the stream, remains authoritative. In a multi-replica
deployment, sticky sessions can keep the explorer and the governed requests it initiates on one replica. If evidence
may be written by other replicas, place a shared backplane (for example, Redis pub/sub) behind
`IGovernanceEvidenceNotifier` so freshness hints cross process boundaries. Sticky sessions alone do not relay hints
created by another process. The explorer must still re-query persisted evidence after a hint because notification
delivery is never the source of truth.

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

## Bounded live-load demonstration

The administrator-only **Bounded live governance load** control evaluates the production composed policy evaluator in
a background task. It does not call the application's HTTP endpoints, so it cannot recursively load the server or turn
the rate limiter into the thing being measured. Only one run can be active in the process, and an administrator can
cancel it at any time.

The checked-in Development configuration enables the control with conservative defaults and hard configured ceilings:

```json
{
  "ReferenceApplication": {
    "GovernanceLoadDemonstration": {
      "Enabled": true,
      "DefaultIterations": 2000,
      "MaximumIterations": 5000,
      "DefaultDurationMilliseconds": 5000,
      "MaximumDurationMilliseconds": 10000
    }
  }
}
```

Configuration itself cannot raise the ceiling above 10,000 decisions or 30 seconds. Start and cancel requests require
the administrator policy and an antiforgery token. The services and routes are registered only when the environment is
exactly `Development` and the switch is enabled.

The browser receives aggregate progress, decisions per second, and a decision-latency histogram through a dedicated
Server-Sent Events stream. These process-local values are labelled **Demonstration, not a benchmark**. Scheduling,
diagnostics, browser timing, and concurrent development activity can all affect them. Use the reproducible
[governance-overhead benchmarks](../benchmarks/governance-overhead.md) for measured host costs.

Demonstration evaluations do **not** invoke the decision-receipt sink and persist no synthetic evidence. The normal
explorer actions still use the real endpoint pipeline and write their evidence to the disposable demonstration
database. This separation keeps the evidence timeline meaningful while the load control exercises the same composed
policy evaluator used by the host.

## Run the walkthrough

1. Start the web project in Development and open `/Account/Login`.
2. Apply the demo administrator role and, when needed, the consequential capability.
3. Open `/governance-explorer`, run the sample actions, and watch the persisted timeline update.
4. Use **Verify hashes** to compare stored decision evidence with its canonical hash.
5. Use **Reset demonstration database** when you want a fresh walkthrough state.
6. As the administrator, start a bounded live run, watch its transient metrics, and cancel it before completion to
   observe both terminal paths.

See [Development-only demo identity controls](development-demo-identity.md) for the fixed claims and safety boundary.
Real performance measurements are documented under [Governance benchmarks](../benchmarks/governance-overhead.md); the
interactive explorer is a demonstration, not a benchmark.

## Verification

The integration tests use temporary SQLite files and the real migration history. They prove that the Development
administrator reset clears all demonstration records and evidence, leaves a migrated empty schema, and requires an
antiforgery token. Load-demonstration tests prove single-run concurrency, administrator authorization, cancellation,
iteration and duration enforcement, aggregate metric publication, and complete service and route absence in Staging
and Production.
