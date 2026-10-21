---
title: "Step 6: Persist and Verify Audit Evidence"
description: How governance decision evidence is persisted through the host-owned DbContext and linked in both directions with NCAT mutation audit and audit completion.
---

# Step 6: Persist and Verify Audit Evidence

**Status:** Complete with AsiBackbone `7.2.1`. Released in [`v0.1.0-alpha.6`](https://github.com/AsiBackbone/ReferenceApplication/releases/tag/v0.1.0-alpha.6).

## Outcome

Every governed endpoint now leaves durable evidence in the host-owned database, and a governed change is linked to the
decision that permitted it in both directions:

| Evidence | Where it lives | Written by |
|:---|:---|:---|
| Governance decision receipt | `AsiBackboneAuditLedgerRecords` (with reason codes and metadata) | `LedgerDecisionReceiptSink`, before the endpoint executes |
| Application change | `GovernedOperationRecords` | The governed endpoint, inside NCAT's audited transaction |
| Mutation audit | NCAT `AuditRecords`, carrying `DecisionAuditRecordId`, `OperationExecutionId`, correlation, and trace | NCAT's save pipeline |
| Audit completion | NCAT `ApplicationAuditCompletionOutboxEntries` | Staged in the same transaction as the change |
| Execution lifecycle event | `AsiBackboneAuditResidueLifecycleEvents` | `AsiBackboneAuditCompletionPublisher`, when the outbox dispatches |

The host still owns the database provider, connection string, migrations, and retention policy. AsiBackbone contributes
its tables to the host model; it does not bring its own `DbContext` or migrations.

## 1. Add the AsiBackbone model to the host context

`ApplicationDbContext.OnModelCreating` applies AsiBackbone's EF Core configuration alongside NCAT's:

```csharp
_ = modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
_ = modelBuilder.ApplyAsiBackboneConfigurations();
```

The context also exposes `AsiBackboneAuditLedgerRecords`, `AsiBackboneDecisionReceiptLifecycleEvents`, and the new
`GovernedOperationRecords` set. `GovernedOperationRecord` is a small `DataEntity` that gives the governed operation a
real, NCAT-audited change to make.

### Keep evidence exactly as it was created

NCAT's save pipeline treats every tracked entity as application data. Before saving, it canonicalizes strings (Unicode
NFC normalization and HTML-entity decoding), truncates `*Utc` timestamps to milliseconds, and writes a mutation-audit
record for each change. Applied to AsiBackbone evidence, that would alter stored values after they were created, which
breaks hash and signature verification, and it would audit the audit trail itself.

`AccountabilityRecordTypes` identifies NCAT's own `AuditRecord` and every AsiBackbone persistence entity as
accountability evidence. The save pipeline skips canonicalization, timestamp normalization, and mutation auditing for
them, and the model's UTC-precision convention leaves AsiBackbone's column configuration to AsiBackbone. Application
data, including `GovernedOperationRecord`, is normalized and audited exactly as before.

### Apply the host-owned migration

The repository already contains the Step 6 migration,
`src/ReferenceApplication.Infrastructure/Data/Migrations/*_AddGovernedAuditEvidence.cs`. It only creates tables: the
AsiBackbone audit ledger, reason-code, metadata, acknowledgment, outbox, and lifecycle tables, plus
`GovernedOperationRecords`. It does not alter any existing NCAT table.

Apply it with the same explicit procedure as the inherited history, described in
[Create the development database](local-database-setup.md). From the repository root:

```powershell
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

`migrations list` shows `AddGovernedAuditEvidence`, marked `(Pending)` until `database update` applies it. It is
followed by `AddCapabilityGrantUses`, which adds the replay table for the
[signed capability tokens](signed-capability-tokens.md) supplement; `database update` applies both. The application
does not apply migrations automatically at startup, and governed operations fail closed until the migration is applied,
because they cannot store their decision evidence.

### How the migration was created

> [!NOTE]
> Do not run this command in a clone of this repository. The migration already exists, so `dotnet-ef` stops with
> `The name 'AddGovernedAuditEvidence' is used by an existing migration.` and changes nothing. The command is shown for
> applying the same pattern to your own NCAT-based application.

After applying `ApplyAsiBackboneConfigurations()` in its `DbContext`, a host generates its own migration:

```powershell
Push-Location src/ReferenceApplication.Web

dotnet tool run dotnet-ef migrations add AddGovernedAuditEvidence `
  --project ../ReferenceApplication.Infrastructure `
  --startup-project . `
  --context ApplicationDbContext `
  --output-dir Data/Migrations

Pop-Location
```

Review the generated migration before applying it. For this step, the review confirmed it creates tables only.

## 2. Persist decision receipts

AsiBackbone's endpoint middleware writes a decision receipt to the registered `IDecisionReceiptSink` for every endpoint
marked `[EmitGovernanceAudit]`, before the endpoint executes. AsiBackbone ships no EF Core sink, so the host provides
`LedgerDecisionReceiptSink`. It converts the receipt with `AuditLedgerRecord.FromDecisionReceipt` and appends it through
AsiBackbone's `EfCoreAuditLedgerStore`.

- **Isolation.** Each append uses a short-lived context from `IDbContextFactory<ApplicationDbContext>`, so persisting
  evidence never saves, or rolls back with, changes tracked by another component.
- **Fail closed.** If the record cannot be stored, the sink throws `GovernanceEvidencePersistenceException` with a
  sanitized reason code. The governed endpoint does not execute. Provider details stay in the host log.
- **Request evidence.** On success, the sink records a `GovernanceDecisionEvidence` for the request: the ledger record
  identifier, a new operation execution identifier, and the decision's correlation and trace identifiers. A request can
  record evidence only once.

### Hash each decision record

Before append, the sink builds AsiBackbone's canonical audit-ledger payload and stores its SHA-256 hash in
`RecordHash`. The canonical payload binds the record identity and decision fields, including timestamps at their stored
precision. Recomputing that hash therefore detects changes such as HTML-entity or Unicode substitutions and
sub-millisecond timestamp edits. Records written before hashing was enabled keep a null `RecordHash`; verification
reports those records as `not-hashed`, not as a mismatch.

The Reference Application deliberately leaves `PreviousRecordHash` null. Every request writes evidence through its own
short-lived context, and safely maintaining a single hash chain across concurrent requests and application instances
would require serialized writes or a separately coordinated chain head. Independent record hashes preserve concurrent
append behavior while still detecting a change relative to the hash stored with that record.

An administrator can recompute the hashes for a governed request with:

```text
GET /api/v1/governance-evidence/{correlationId}/verification
```

Each record result is `verified`, `mismatch`, or `not-hashed` and includes the stored and recomputed hash when available.
This endpoint uses the same administrator policy as the evidence timeline API.

> [!IMPORTANT]
> A matching hash proves only that the current canonical fields match the hash currently stored beside them. It is not
> a digital signature, trusted timestamp, immutable external anchor, or proof of who wrote the record. An attacker who
> can replace both the database record and its hash can manufacture a new match. Signing and external anchoring remain
> separate deployment decisions.

All governed POST operations now emit audit, so allowed decisions, denials, and acknowledgment challenges are all
recorded:

```csharp
[HttpPost("records")]
[ValidateAntiForgeryToken]
[EmitGovernanceAudit]
[GovernancePolicy(typeof(AdministratorActionPolicy))]
public async Task<ActionResult<GovernedRecordCreatedResponse>> CreateRecord(...)
```

## 3. Link the mutation audit to the decision

NCAT's `HttpContextApplicationAuditContextAccessor` now reads the request's governance evidence. When it is present, the
audit context for every mutation in that request carries the decision's `DecisionAuditRecordId`, the request's
`OperationExecutionId`, and the decision's own correlation and trace identifiers. No endpoint has to wire this in.

The new `POST /api/v1/governed-operations/records` endpoint shows the full pattern. It runs its change inside NCAT's
audited transaction and stages the audit-completion entry in the same transaction:

```csharp
ApplicationAuditedTransactionResult result = await auditedTransaction.ExecuteAsync(
    (dbContext, token) => { dbContext.GovernedOperationRecords.Add(record); return Task.CompletedTask; },
    async (dbContext, receipt, token) => await completionOutbox.StageAsync(dbContext, receipt, cancellationToken: token),
    cancellationToken: cancellationToken);
```

The response returns the record identifier, the decision audit record identifier, the operation execution identifier,
and the NCAT mutation batch identifier.

## 4. Deliver audit completion as AsiBackbone lifecycle evidence

`AsiBackboneAuditCompletionPublisher` is the host-owned form of AsiBackbone's optional
[NCAT audit-completion adapter](https://asibackbone.github.io/AsiBackbone/articles/ncat-audit-completion-adapter.html).
`AddApplicationGovernanceEvidence` enables NCAT's audit-completion outbox with `asibackbone` as its default destination
and registers the publisher for it. When the outbox dispatches an entry, the publisher:

1. validates the message and maps NCAT's persistence outcome;
2. loads the decision from the audit ledger, deferring delivery if it is not stored yet;
3. rejects a completion whose correlation or trace identifier differs from the decision's;
4. builds a `GovernedOperationExecutionReceipt` and appends a `GatewayExecutionCompleted` lifecycle event that carries the
   mutation batch, record count, manifest hash and algorithm, outcome, and decision audit record identifier.

Delivery is at least once. The lifecycle event identifier is derived from NCAT's idempotency key, so a redelivered entry
is acknowledged as a duplicate, and different evidence under the same key is a terminal `idempotency-conflict`.

| Result | NCAT outbox disposition |
|:---|:---|
| Event appended, or an equivalent event already exists | Delivered |
| Decision not stored yet | Deferred |
| Lifecycle append failed | Retryable failure |
| Invalid message, unsupported outcome, identifier mismatch, or idempotency conflict | Failed |

The outbox options can be overridden in the `ReferenceApplication:AuditCompletionOutbox` configuration section.

## Registration

`Program.cs` adds the evidence services after data access and the AsiBackbone integration:

```csharp
builder.Services.AddApplicationDataAccess(builder.Configuration);
builder.Services.AddApplicationAsiBackbone();
builder.Services.AddApplicationGovernanceEvidence();
```

`AddApplicationGovernanceEvidence` binds AsiBackbone's EF Core audit ledger and lifecycle stores to `ApplicationDbContext`,
registers the evidence holder, the receipt sink, and the completion publisher, and enables the NCAT audit-completion
outbox.

## Observe persisted evidence notifications

Administrators can open `GET /api/v1/governance-evidence/notifications` as a Server-Sent Events stream. Supply an
optional exact `correlationId` query value to filter the stream; without it, an administrator receives all new
notifications in that application process. The endpoint uses the same administrator policy as the evidence query API,
handles a disconnected client as normal cancellation, and needs no CSP exception or client library.

Each `evidence` event contains only three identifiers:

```json
{
  "correlationId": "0HNP...",
  "kind": "mutation-audit",
  "recordId": "mutation-batch-id"
}
```

The possible kinds are `decision`, `mutation-audit`, and `lifecycle-event`. The client uses the correlation identifier
to re-query `GET /api/v1/governance-evidence/{correlationId}`; notifications never duplicate the evidence payload.

The in-process stream is a best-effort freshness hint, not a durable queue. Each subscriber has a bounded buffer and a
slow client can miss a hint, so the database-backed evidence API remains authoritative. Notification publishing occurs
only after the corresponding evidence is persisted. A notifier failure is logged and cannot fail or delay the governed
request or audit-completion outbox delivery.

## Explore the evidence in a browser

After applying the Step 6 migration, sign in and open `/governance-explorer` (also linked from the application home
page). The two-pane explorer calls the real governed endpoints with antiforgery protection and renders the persisted
timeline returned by the evidence API. An administrator sees decision receipts, application and mutation records,
audit-completion state, and lifecycle events. The Server-Sent Events stream re-queries that timeline after each
identifier-only notification, so the delivered lifecycle event appears without a page reload when the outbox processes
the record.

Authenticated non-administrators can run the governed operations and see their HTTP outcomes, including denials, but
the full ledger and hash verification remain administrator-only. The **Verify hashes** action calls the verification
API described above and reports each decision record as `verified`, `mismatch`, or `not-hashed`.

For local walkthroughs, enable the existing Development-only demo identity control:

```json
{
  "ReferenceApplication": {
    "DemoIdentity": {
      "Enabled": true
    }
  }
}
```

The explorer then shows fixed administrator-role and consequential-capability toggles. Applying them replaces only the
temporary demo cookie; the route remains unavailable outside Development or when the switch is false. The
consequential-action checkbox is an explicit UI gate for requesting the demonstration challenge. It does not bypass
governance or turn acknowledgment into authorization—the sample endpoint still returns `428 Precondition Required`,
and a production continuation workflow remains host-owned.

The page keeps the existing Content Security Policy: its JavaScript and CSS are static files under `wwwroot`, with no
inline scripts or styles. Every control is keyboard operable, focus is visible, action and connection status use a
polite live region, and the evidence timeline announces persisted updates.

## Verification

The walkthrough Step 6 tests use real SQLite databases created from the application's migrations.

- `GovernedAuditEvidenceTests` drive the complete web host. An administrator's `POST …/records` produces:
  - an `Allowed` ledger record with the Step 4 policy version and hash;
  - the new record;
  - an NCAT mutation-audit record carrying the decision's identifiers;
  - a staged completion entry;
  - after dispatch, the linked lifecycle event. A second dispatch delivers nothing.

  The same class also verifies that:
  - a denied request records the denial and changes nothing;
  - every governed operation persists exactly one decision per request with its outcome;
  - anonymous and ungoverned requests persist no decision;
  - an invalid request makes no change;
  - a governed request fails closed when its decision cannot be stored;
  - a subscriber receives the decision, mutation-audit, and lifecycle-event notifications in persistence order;
  - a throwing notifier does not affect the governed request or completion delivery;
  - anonymous and non-administrator clients cannot open the notification stream.
  - a new decision hash verifies successfully;
  - direct canonical-text and sub-millisecond timestamp changes produce a mismatch;
  - a legacy record without a hash is reported as `not-hashed`.
- `GovernanceEvidenceControllerTests` verify the identifier-only SSE wire format and normal client-disconnect handling.
- `GovernanceExplorerTests` verify authorized and anonymous page access, the administrator evidence boundary,
  antiforgery and accessible-live-region markup, external-only scripts and styles, and the static assets.
- `AccountabilityRecordPersistenceTests` verify the model and migrations, including that the migrations match the
  current model. They also verify that AsiBackbone evidence is stored byte-for-byte with sub-millisecond timestamps and is
  never mutation-audited, while application data in the same context is still canonicalized, normalized, and audited.
- `LedgerDecisionReceiptSinkTests` cover field mapping, canonical record hashing, request evidence, one decision per
  request, and failing closed on both rejected and thrown persistence errors.
- `AsiBackboneAuditCompletionPublisherTests` cover delivery, duplicates, idempotency conflicts, deferral, correlation
  checks, validation, outcome mapping, and registration.

```powershell
dotnet restore ReferenceApplication.slnx --locked-mode
dotnet build ReferenceApplication.slnx --configuration Release --no-restore
dotnet test ReferenceApplication.slnx --configuration Release --no-build --no-restore
```

## Authoritative references

- [AsiBackbone EF Core Host Ownership and Migrations](https://asibackbone.github.io/AsiBackbone/articles/ef-core-host-ownership-and-migrations.html)
- [AsiBackbone Optional NCAT Audit-Completion Adapter](https://asibackbone.github.io/AsiBackbone/articles/ncat-audit-completion-adapter.html)
- [NCAT Audit Accountability Integration](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/audit-accountability-integration.html)
- [NCAT Data Access](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/data-access.html)
