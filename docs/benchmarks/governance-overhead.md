---
title: "Governance Overhead Benchmarks"
description: Repeatable BenchmarkDotNet measurements for the governance integration owned by the Reference Application host.
---

# Governance overhead benchmarks

The Reference Application benchmarks the cost that this host adds around AsiBackbone. These measurements complement,
and do not duplicate, the framework's [performance benchmark baseline](https://asibackbone.github.io/AsiBackbone/articles/performance-benchmark-baseline.html).

## Run the benchmark

Run every scenario from the repository root:

```powershell
dotnet run -c Release --project tests/ReferenceApplication.Benchmarks -- --filter "*"
```

BenchmarkDotNet reports the mean, statistical error, standard deviation, and allocated bytes for every scenario. The
`Governance Benchmarks` GitHub Actions workflow runs weekly and on demand, then uploads the complete
`BenchmarkDotNet.Artifacts` directory. The workflow does not run for pull requests or ordinary pushes because shared
runner timing is evidence for trends, not a merge gate.

The benchmark executable is marked `IsTestProject=false`. It is compiled by the solution build, but `dotnet test` does
not execute it and the project has no coverage collector dependency. Normal test and coverage gates therefore remain
independent from machine-specific timing results.

## Measured host paths

| Scenario | Host-owned work included |
| --- | --- |
| `host_policy.administrator_and_consequential` | The production composed evaluator, administrator-role constraint, capability constraint not-applicable path, and consequential-action decision policy. |
| `host_policy.capability_scope` | The production composed evaluator, capability-scope declaration constraint, administrator constraint not-applicable path, and decision policy. |
| `host_evidence.receipt_to_sqlite_ledger` | `DecisionReceipt` mapping, host evidence hashing, and `LedgerDecisionReceiptSink` append to SQLite. |
| `host_evidence.audit_completion_delivered` | NCAT completion mapping, decision lookup, lifecycle-event construction, first append, and host notification dispatch. |
| `host_evidence.audit_completion_duplicate` | NCAT completion mapping, decision and lifecycle lookups, reconstruction, equivalence comparison, and idempotent duplicate acknowledgment. |

Schema migration and per-scenario fixture creation happen outside measured operations. Persistence scenarios use a
temporary file-backed SQLite database in write-ahead logging (WAL) mode with connection pooling disabled. Each measured
operation uses the production dependency-injection registrations and a fresh application scope.

## Current reference snapshot

The following snapshot was recorded on October 19, 2026. Treat it as a reproducibility record, not a performance
target or service-level objective.

| Environment | Value |
| --- | --- |
| CPU | AMD Ryzen 5 7430U, 6 physical cores / 12 logical cores |
| Operating system | Windows 11 25H2, build 26200.9711 |
| SDK | .NET SDK 10.0.402 |
| Runtime | .NET 10.0.13, x64 RyuJIT x86-64-v3, concurrent workstation GC |
| BenchmarkDotNet | 0.15.8 |
| SQLite mode | Temporary file, WAL journal, shared cache, connection pooling disabled |

| Scenario | Mean | Error (99.9% CI half-width) | Allocated per operation |
| --- | ---: | ---: | ---: |
| `host_policy.administrator_and_consequential` | 506.6 ns | 26.06 ns | 1,640 B |
| `host_policy.capability_scope` | 234.2 ns | 6.59 ns | 320 B |
| `host_evidence.receipt_to_sqlite_ledger` | 6.679 ms | 0.343 ms | 163.95 KB |
| `host_evidence.audit_completion_delivered` | 10.757 ms | 0.951 ms | 136.79 KB |
| `host_evidence.audit_completion_duplicate` | 4.655 ms | 0.530 ms | 114.16 KB |

The persistence job uses 24 operations per iteration, 3 warmup iterations, and 10 measurement iterations. The policy
job uses BenchmarkDotNet's adaptive invocation count, 3 warmup iterations, and 8 measurement iterations. The relatively
wide error interval for duplicate delivery is another reason to compare several runs before drawing conclusions from a
small change.

## Boundaries

These benchmarks intentionally do **not** measure:

- AsiBackbone's framework-only hot paths, which belong to the linked framework baseline;
- HTTP transport, authentication, antiforgery, serialization, security headers, or the global rate limiter;
- concurrent SQLite writers, network storage, a production database server, or server saturation;
- browser rendering or the governance explorer user interface; or
- end-to-end request throughput and latency.

The results isolate integration cost well enough to detect regressions in host rules and evidence adapters. They do not
predict production request latency. Compare runs only on equivalent hardware, operating systems, .NET versions, SQLite
modes, repository revisions, and BenchmarkDotNet job settings. Prefer several repeated runs before acting on a small
difference.
