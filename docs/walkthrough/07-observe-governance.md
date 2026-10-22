---
title: "Step 7: Observe governance in action"
description: Use the Development governance explorer to run the real governed endpoints and inspect their persisted evidence.
---

# Step 7: Observe governance in action

**Status:** Complete. The explorer combines evidence query, live notifications, verification, a resettable demonstration
database, a bounded in-process load demonstration, a policy simulator, decision replay, a pipeline inspector, and
preset failure scenarios.

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

### Layout

The controls sit on the left in tabs, and the right-hand panel shows the output of the active tab:

| Tab | Controls | Right-hand output |
| --- | --- | --- |
| **Actions** | the governed action buttons and their status | the evidence timeline |
| **Demo identity** | the fixed demo claims and the database reset | the evidence timeline |
| **Load demonstration** | the bounded run and its status | the live metrics and latency histogram |
| **Policy simulator** | the simulation inputs and their status | the staged simulation result |
| **Decision replay** | the receipt lookup, recent decisions, and candidate policy | the decision, its reconstruction, and the side-by-side replay |
| **Pipeline inspector** | the request to follow, the outbox filter, and live refresh | stage timing, the outbox, reconciliation, and telemetry |
| **Failure scenarios** | the preset scenarios, each with what it teaches and expects | the outcome, the decision, and the evidence left behind |

A tab appears only when its feature is available, so outside Development the explorer shows the actions alone. The
explorer remembers the selected tab for the browser session, except after a database reset, which returns to
**Actions**. **Send for real** in the simulator switches the right-hand panel to the evidence timeline; select the
**Policy simulator** tab again to return to the simulated receipt.

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

## Policy simulator

The administrator-only **Policy simulator** shows how a decision is built, one stage at a time. The explorer's action
buttons show only the outcome; the simulator lets you change the inputs and watch each stage respond.

Build a request from fixed options:

- the governed operation: administrator, capability, consequential, or create audited record;
- the simulated actor's administrator role and consequential capability, the same fixed claims the demo identity
  offers, with your own actor identifier;
- the acknowledgment flag, which, as in the explorer, only enables the consequential request; and
- a sample payload, posted as the record title or as a `Note` field.

You can also clear individual constraints to see what each contributes. The result is then labelled **Simulated with a
modified policy**, and it cannot be sent for real.

**Evaluate** reports four stages:

1. each constraint's outcome and reason codes, in evaluation order;
2. the decision composed from them, and the decision-policy step, for example the consequential action becoming
   acknowledgment-required;
3. the capability-grant check. With permission claims, the claim validator's result. With
   [signed capability tokens](signed-capability-tokens.md), the token issuance would sign: its subject, operation,
   scopes, payload binding, validity window, and the identifiers of the signing key, never the key itself; and
4. the decision receipt that would be appended to the ledger, with the policy version and hash.

It also says what the real endpoint would do: run the action, block it with a status code, or answer with an
acknowledgment challenge.

### How it stays faithful

The simulator does not reimplement governance. It resolves the real governed endpoint, builds a simulated request for
it, and runs AsiBackbone's `DefaultEndpointGovernanceService`, the same service the governance middleware runs. The
policy evaluator is composed by the same method as the registered one. The simulator wraps each constraint, the
decision policy, and the capability validator only to record what they return. It does not call the governed HTTP
endpoint, as the load demonstration does not.

### What it never does

Nothing is persisted. The decision receipt sink is replaced with one that captures the receipt instead of writing it,
so no ledger, outbox, lifecycle, or mutation-audit record is created. With signed tokens enabled, the signed-grant
validator, which would consume a use, is replaced with the token preview, so the use store is untouched. The simulated
request runs on an isolated asynchronous flow, so services that read the current request see the simulated one and
never the real one.

### Send for real

**Send for real** posts the same inputs through the real endpoint, so you can compare the simulated receipt with the
persisted decision in the evidence timeline. It is available only when the comparison is meaningful:

- every constraint is in the policy;
- the simulated role and capability match the signed-in actor; apply a matching demo identity first; and
- the operation needs no capability token. The explorer does not request tokens yet, so with signed tokens enabled the
  capability-protected operations cannot be sent from here.

### Configure

The checked-in Development configuration enables the simulator:

```json
{
  "ReferenceApplication": {
    "PolicySimulator": {
      "Enabled": true
    }
  }
}
```

The service and route are registered only when the environment is exactly `Development` and the switch is enabled.
`POST /api/v1/policy-simulator/evaluate` requires the administrator policy and an antiforgery token.

## Decision replay

The administrator-only **Decision replay** opens a past decision and asks two questions of it: does it still hold under
the current policy, and what would a policy change have done to it?

Find a decision by its receipt identifier, or pick one from **Recent decisions**, newest first, with **Load older** for
earlier pages. Before anything is replayed, the right-hand panel shows the persisted decision: its operation, actor,
outcome and reason codes, the policy version and hash it was recorded under, and its evidence hash with the result of
recomputing it.

**Replay selected decision** always replays against the current policy. Optionally, it also replays against a
candidate: leave registered constraints out, choose the stand-in policy, or both. The results appear side by side under
the recorded decision, with a changed outcome highlighted and reason codes marked as added or removed.

### What the ledger can reconstruct

The ledger stores minimized evidence, not the request. Replay rebuilds the evaluation context from what is stored, and
the panel labels each input as recorded, inferred, or not recorded:

| Input | Stored? | How replay obtains it |
| --- | --- | --- |
| Operation | Recorded | The receipt's operation name, mapped to the sample endpoint |
| Endpoint declarations: policy markers and capability scopes | Recorded | The receipt metadata |
| Actor identifier and type | Recorded | The receipt |
| Policy version and hash; outcome and reason codes | Recorded | The receipt |
| Actor's administrator role | Not recorded | Inferred from the reason codes when they determine it |
| Actor's capability grant | Not recorded; an accepted grant's identifier is | Inferred from the grant identifier, a grant denial, or an outcome the capability check let through |
| Capability token | Not recorded | Never re-verified: with signed tokens enabled, the recorded grant outcome is used |
| Request payload | Not recorded | Not needed: the policy does not read it |

Inference follows how the policy runs. For example, a decision denied with
`referenceapplication.actor.administrator_required` shows the actor lacked the role, and an allowed administrator action
shows they had it. The capability check runs for every decision that is not already denied, so one that was not denied
passed it. Positive role inference is made only when the recorded policy hash is the current one.

When a replay needs a fact that cannot be inferred, it is evaluated under each value instead of being guessed. If every
value gives the same decision, the panel says the fact made no difference. Otherwise the replay is marked **partial**
and shows the outcome for each value. For example, a capability action never checks the administrator role, so the
ledger holds no evidence of it. Under the stand-in policy, which adds that requirement, the replay of an allowed
capability action is partial: denied without the role, allowed with it.

### Current policy and candidates

A replay against the current policy of a decision recorded under the current policy hash is a **re-verification**: it
should reproduce the recorded outcome and reason codes. If the recorded hash differs from
`ReferenceApplicationGovernancePolicySet.PolicyHash`, the lookup says so before any replay, and the current-policy
replay is labelled a **what-if**, not a re-verification. The lookup also flags an endpoint whose governance
declarations have changed since the decision.

Candidates are always labelled what-if. The stand-in policy, version 1.1.0 with its own hash, exists only for replay;
the governed endpoints never evaluate it. It changes two things:

- capability-protected actions also require a configured administrator role; and
- creating an audited record also requires explicit acknowledgment. The records endpoint does not declare
  `[RequireAcknowledgment]`, so it cannot present a challenge: the replay shows it blocking the request instead, a
  sign that this policy change would need an endpoint change too.

### What replay never does

Replay is read-only. It runs through the same pipeline harness as the policy simulator, so the decision receipt sink is
never invoked and the receipt is captured instead of appended. No ledger, outbox, lifecycle, mutation-audit, or
capability-use row is written or changed. A token is never verified again, so no use is consumed.

### Configure

The checked-in Development configuration enables decision replay:

```json
{
  "ReferenceApplication": {
    "DecisionReplay": {
      "Enabled": true
    }
  }
}
```

The service and routes are registered only when the environment is exactly `Development` and the switch is enabled.
All three require the administrator policy:

- `GET /api/v1/decision-replay/decisions?before={recordId}` lists decisions, newest first, 25 per page;
- `GET /api/v1/decision-replay/decisions/{decisionReceiptId}` returns one decision and its reconstruction; and
- `POST /api/v1/decision-replay/decisions/{decisionReceiptId}/replay` replays it, and requires an antiforgery token.

## Pipeline inspector

> **Demonstration, not a benchmark.** The inspector shows when each stage of one request happened. The
> [governance benchmarks](../benchmarks/governance-overhead.md) measure what each stage costs.

The administrator-only **Pipeline inspector** shows the main architectural point of the pattern: governance is split
between a short synchronous path and work deferred to the background.

| Stage | Where it runs | Measured cost |
| --- | --- | --- |
| In-memory policy evaluation | On the request path | about 0.2–0.5 µs per decision |
| Decision-receipt append to the SQLite ledger | On the request path, before the action runs | about 6.7 ms |
| Audit-completion delivery: lifecycle event and notification | In the background, after the response | about 10.8 ms |

The figures are means from the benchmark snapshot. The two synchronous stages are what every governed request pays. A
mutation also stages its audit completion in the same transaction, which costs one more row. Delivering it, and
appending the lifecycle event, is left to the outbox worker, so a slow or failing destination delays the evidence but
never the request. The trade-off is a window in which the decision is recorded but its completion is not. The outbox
closes that window with retries and dead-lettering, and reconciliation reports any batch that never closes it.

### What it shows

- **Stage timing** for one request: the latest action the explorer ran, or any correlation identifier you enter. Each
  stage shows its time, its offset from the first, and where the timestamp comes from. A boundary marks where the
  response is sent and background delivery takes over.

  | Stage | Timestamp |
  | --- | --- |
  | Request received; response sent | The ASP.NET Core server span, when OpenTelemetry tracing created one |
  | Decision evaluated; decision receipt appended | The ledger's `OccurredUtc` and `RecordedUtc` |
  | Mutation saved; outbox entry staged | The outbox entry's `ReceiptCompletedUtc` and `CreatedUtc`, committed together |
  | Outbox dispatched | `DeliveredUtc`, or `LastAttemptUtc` while retrying |
  | Lifecycle event appended | The delivery that appended it; the event itself carries the completion time |

- **The audit-completion outbox**: pending, retrying, deferred, delivered, failed, and dead-lettered entries, with
  attempt counts, next-attempt times, the last error, and whether each entry's lifecycle event has been appended.
  Filter it by status.
- **Reconciliation**: the latest summary and the open findings.
- **Telemetry**: the activity sources and meters in the process, whether the host's OpenTelemetry configuration collects
  each, the current values of the host's own gauges, and the spans and metric measurements recorded for the selected
  request.

The inspector refreshes when the evidence stream sends a hint, and polls every 2 seconds while its tab is open and the
page is visible. Polling is what shows retries and dead-letters, which send no hint.

### What it reads, and what it never does

Every read is a query. Stage timing uses timestamps the pipeline already stores, so the inspector adds nothing to the
governed request path. The outbox is read through its query interface and reconciliation through the reconciler, so the
inspector adds no second write path; snapshots change no row.

Telemetry comes from an in-process `ActivityListener` and `MeterListener`, never from the OTLP exporter. The activity
listener never samples, so it creates no spans; it sees only spans OpenTelemetry tracing already created. The meter
listener enables measurements only for the request-duration histogram, when OpenTelemetry already collects it, and for
the host's reconciliation gauges, which it reads on demand. Measurements carry no trace identifier, so the request's
duration measurement is matched by route, method, and time. Spans and measurements are held in bounded buffers of 512
each.

A request's spans are found by the trace identifier on the decision's ledger record. Since AsiBackbone `7.5.0`, every
endpoint decision records the request's trace identifier, including a decision returned by a policy evaluator
([AsiBackbone/AsiBackbone#897](https://github.com/AsiBackbone/AsiBackbone/issues/897)), so the decision alone links the
request to its spans, whether or not the operation changes data. A decision recorded with no W3C trace identifier, such
as one recorded before the upgrade or while tracing was off, shows its request and response stages as not captured.

The inspector shows two gaps in the host's telemetry. The host does not use `AsiBackbone.OpenTelemetry`, so governance
itself emits no spans or metrics; only ASP.NET Core and HttpClient instrumentation is collected. And the
`ReferenceApplication.AuditReconciliation` meter is created but not added to the OpenTelemetry pipeline, so its gauges
are not exported.

### Reconciliation in Development

The host does not otherwise register NCAT audit reconciliation. While the inspector is active, it registers
reconciliation with its background worker, metrics, and the `application-audit-integrity` health check, bound to
`ReferenceApplication:AuditReconciliation`. The checked-in Development configuration runs the worker every minute.
Staging and Production compositions are unchanged.

### Configure

```json
{
  "ReferenceApplication": {
    "PipelineInspector": {
      "Enabled": true
    },
    "AuditReconciliation": {
      "Interval": "00:01:00"
    }
  }
}
```

The service, listener, reconciliation, and route are registered only when the environment is exactly `Development` and
the switch is enabled. `GET /api/v1/pipeline-inspector/snapshot?correlationId={id}&status={status}` requires the
administrator policy and is never cached.

## Failure scenarios

The other tabs show the outcomes a well-formed request produces. The administrator-only **Failure scenarios** tab
triggers common failure and attack paths against the same real endpoints, so you can see what was blocked and what
evidence was left behind. Each scenario shows the HTTP outcome, the decision and its reason codes, whether the governed
action body ran, and the request's evidence timeline.

| # | Scenario | What it teaches | Outcome and evidence |
| --- | --- | --- | --- |
| 1 | Missing capability grant | The capability check runs for every decision that is not already denied, so a missing grant is denied before the action runs. | 403. A denied decision with `referenceapplication.capability.grant_required` (or AsiBackbone's `endpoint.capability_grant.missing` when signed tokens are enabled). |
| 2 | Constraint throws | AsiBackbone's evaluator treats a constraint exception as a denial, so a broken rule fails closed. | 403. A denied decision with `asibackbone.policy.constraint_exception`. No record. |
| 3 | Ledger append fails | A governed action never runs without its decision evidence. | 500. `GovernanceEvidencePersistenceException` with `asi_backbone.audit_ledger.append_failed`. No ledger row, no record. |
| 4 | Outbox delivery fails | A failing destination delays evidence, never the request. | 201. The outbox entry retries on its backoff schedule and, once the fault is cleared, is delivered with its lifecycle event. |
| 5 | Execution fails after approval | A decision records permission, not execution. | 500. An allowed decision with no mutation audit, outbox entry, or lifecycle event. |
| 6 | Evidence tampering | Each ledger record carries a hash of its canonical payload. | An in-memory copy with a changed outcome reports a hash mismatch; an unchanged copy and the stored row verify. |
| 7 | Payload tampering under a valid token | A signed token is bound to the payload it was issued for. | 403. A denied decision with `endpoint.capability_grant.resource_mismatch`. |
| 8 | Expired token replay | A token is honored only inside its validity window. | 403. A denied decision with `endpoint.capability_grant.expired`. |

The issue proposed `policy.constraint_exception_as_denial` for scenario 2. AsiBackbone's actual default, verified in the
tests, is `GovernancePolicyOptions.DefaultConstraintExceptionReasonCode`, `asibackbone.policy.constraint_exception`.

Scenarios 7 and 8 need signed capability tokens ([Signed capability tokens](signed-capability-tokens.md)). While
`ReferenceApplication:CapabilityTokens:Enabled` is false they are listed but disabled, with that explanation. They issue
a real token for the current actor after the issuance endpoint's entitlement check, so the actor also needs the
consequential capability. Scenario 8 issues its token from a clock set back past the token lifetime and the validator's
clock skew, instead of waiting for it to expire.

### Execution failure and reconciliation

Scenario 5 shows where the evidence chain has a deliberate gap. The decision is appended before the action runs, so
it records that the request was permitted. The mutation, its NCAT audit, and its outbox entry then commit in one
transaction, so when the mutation fails all three roll back together. Reconciliation compares committed mutation-audit
batches with their audit completions, so it has nothing about this request to report: there is no batch. The gap shows
in the ledger instead, as an allowed decision with no lifecycle event. A consumer that needs proof of execution reads
the lifecycle event, not the decision.

### How the faults are injected

Arming a scenario returns the request to send, including an `X-Failure-Scenario-Run` header that names the run. The
explorer then sends that request to the real governed endpoint. Nothing about the governed path changes for any other
request:

- **One request, one actor.** Middleware between authorization and endpoint governance consumes the run when the
  request arrives. A run belongs to the actor who armed it, expires after two minutes, and is used once; a second
  request with the same run is refused with 409 before governance runs.
- **Seams at the real failure points.** Scenario 1 removes the capability claim from that one request's principal.
  Scenario 2 is a Development-only constraint that throws when its scenario is armed and otherwise does not apply.
  Because it is registered like any other constraint, the policy simulator and decision replay list it, as not
  applicable, while the scenarios are active.
  Scenarios 3, 4, and 5 use an EF Core save interceptor that fails the save that appends the ledger record, the
  lifecycle event, or the governed record, so AsiBackbone's ledger store, the audit-completion publisher, and NCAT's
  audited transaction each handle a real `DbUpdateException`.
- **Delivery faults last until cleared.** Scenario 4's fault applies only to the armed request's audit completion, on
  every delivery attempt, until **Clear the delivery fault** is selected. Left alone, the entry is dead-lettered after
  the outbox's maximum attempts, which is also worth seeing.
- **Read-only tampering.** Scenario 6 sends no request. It reads the newest decision, or the one the explorer
  last selected, builds two in-memory copies, changes the outcome on one, and verifies both with the same method the
  **Verify hashes** button uses. It then verifies the stored record again to show it was not modified.

Runs are kept in memory, bounded to the 32 most recent. Their evidence is whatever the governed requests wrote to the
demonstration database; reset it from the **Demo identity** tab.

### Configure

```json
{
  "ReferenceApplication": {
    "DemoIdentity": {
      "Enabled": true
    },
    "FailureScenarios": {
      "Enabled": true
    }
  }
}
```

The scenarios, the throwing constraint, the save interceptor, the middleware, and the routes are registered only when
the environment is exactly `Development`, the switch is enabled, and the Development demo identity has selected the
disposable demonstration database. The routes under `/api/v1/failure-scenarios` require the administrator policy; arming
a scenario and clearing a delivery fault also require an antiforgery token, and run reports are never cached.

## Run the walkthrough

1. Start the web project in Development and open `/Account/Login`.
2. Apply the demo administrator role and, when needed, the consequential capability.
3. Open `/governance-explorer`, run the sample actions on the **Actions** tab, and watch the persisted timeline
   update.
4. Use **Verify hashes** to compare stored decision evidence with its canonical hash.
5. Use **Reset demonstration database** when you want a fresh walkthrough state.
6. As the administrator, on the **Load demonstration** tab, start a bounded live run, watch its transient metrics,
   and cancel it before completion to observe both terminal paths.
7. On the **Policy simulator** tab, evaluate an operation, clear a constraint to see what it contributes, then
   restore it and send the request for real to compare the simulated receipt with the persisted decision.
8. On the **Decision replay** tab, pick a recent decision and replay it to confirm it still holds. Then choose the
   stand-in policy and replay an allowed capability action to see a partial what-if.
9. Open the **Pipeline inspector** tab, then create an audited record on the **Actions** tab. Back on the inspector,
   watch the record's outbox entry move from pending to delivered within the worker's poll interval, and the lifecycle
   event appear, without reloading.
10. On the **Failure scenarios** tab, run scenario 4 and watch its delivery fail and retry, then clear the fault and
    watch it deliver. Run scenario 5, then scenario 6 to verify a tampered copy of the decision scenario 5 left behind.

See [Development-only demo identity controls](development-demo-identity.md) for the fixed claims and safety boundary.
Real performance measurements are documented under [Governance benchmarks](../benchmarks/governance-overhead.md); the
interactive explorer is a demonstration, not a benchmark.

## Verification

The integration tests use temporary SQLite files and the real migration history. They prove that the Development
administrator reset clears all demonstration records and evidence, leaves a migrated empty schema, and requires an
antiforgery token. Load-demonstration tests prove single-run concurrency, administrator authorization, cancellation,
iteration and duration enforcement, aggregate metric publication, and complete service and route absence in Staging
and Production.

`PolicySimulatorTests` prove that, for every sample operation and several simulated actors, the simulated receipt
carries the outcome and reason codes the real endpoint then persists for the same inputs, and that the simulated
endpoint result matches the real response. They also prove that clearing a constraint changes the result and marks it
as simulated with a modified policy, and that no simulation writes ledger, outbox, lifecycle, mutation-audit, or
capability-use records. The signed-token preview shows the grant's claims, payload binding, and expiry without key
material. The panel and route are absent outside Development, and rejected for non-administrators and for requests
without an antiforgery token.

`DecisionReplayTests` prove that a decision is found by its receipt identifier and an unknown one returns not found;
that, for every sample operation and several actors, replaying the persisted decision under the current policy
reproduces its outcome and reason codes; and that this also holds for a grant denial recorded with signed tokens
enabled. They prove that a candidate with a constraint left out, or the stand-in policy, changes the outcome and is
labelled hypothetical, and that a replay needing an unrecorded fact is partial with a branch for each value. A decision
recorded under a different policy hash is flagged in the list and the lookup before replay. Ledger, outbox, lifecycle,
mutation-audit, application-record, and capability-use row counts are unchanged after replays. The routes and panel are
absent outside Development and rejected for non-administrators, and a replay without an antiforgery token is rejected.

`PipelineInspectorTests` prove that a created record's outbox entry is shown pending and then delivered, with the
lifecycle event its delivery appended, and that a failed delivery is shown with its attempt count, last error, and next
attempt. One request's stages come in pipeline order with the synchronous stages first, and the persisted timestamps are
in that order. Reconciliation reports a missing completion as an open finding. The telemetry view lists the sources and
meters present, marks which OpenTelemetry collects, reads the host's gauges, and shows the request's server span.
Snapshots leave every outbox entry and evidence row unchanged. The inspector, its listener, the reconciliation it
registers, and its route are absent outside Development, and the route is rejected for non-administrators.

`FailureScenarioTests` run each scenario through the real endpoint and assert its HTTP outcome, reason code, and
evidence shape: the recorded decision and its reason codes, the ledger, record, mutation-audit, and outbox row counts,
and, for scenario 4, the retry, the recovery after the fault is cleared, and the lifecycle event. The governed action
body runs only in scenarios 4 and 5, and scenario 5's change is rolled back. A run is refused once used, and a request
without the run header is unaffected while a run is armed. The token scenarios are listed as unavailable while tokens
are disabled. The scenarios, the throwing constraint, the save interceptor, and the routes are absent in Staging and
Production, and in Development while the switch is off or the demonstration database is not in use, and the routes are
rejected for non-administrators. `FailureScenarioRegistryTests` prove that a run is bound to its actor, expires unused,
and is used once, and that a delivery fault applies only to its own request until cleared.
