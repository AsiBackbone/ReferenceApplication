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
`BenchmarkDotNet.Artifacts` directory. Every successful workflow run also writes
`BenchmarkDotNet.Artifacts/benchmark-provenance.md`, which records the exact repository commit, repository ref,
AsiBackbone package version, and workflow run ID. The workflow does not run for pull requests or ordinary pushes
because shared runner timing is evidence for trends, not a merge gate.

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

## Historical reference snapshot

The following snapshot was recorded on October 19, 2026. It predates the current AsiBackbone `7.5.0` integration and
the host's signed-capability-token work, so it is retained only as a historical reproducibility record. It must not be
presented as the stable-release benchmark snapshot or as evidence for the current host behavior.

Treat these values as a reproducibility record, not a performance target or service-level objective.

| Environment | Value |
| --- | --- |
| CPU | AMD Ryzen 5 7430U, 6 physical cores / 12 logical cores |
| Operating system | Windows 11 25H2, build 26200.9711 |
| SDK | .NET SDK 10.0.402 |
| Runtime | .NET 10.0.13, x64 RyuJIT x86-64-v3, concurrent workstation GC |
| BenchmarkDotNet | 0.15.8 |
| AsiBackbone | 7.2.1 |
| Repository revision | [`73768d2bdee1acec8b2b1d9473a6b4e757fbe0bc`](https://github.com/AsiBackbone/ReferenceApplication/commit/73768d2bdee1acec8b2b1d9473a6b4e757fbe0bc) |
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

## Stable-release snapshot refresh

Before benchmark results are used as stable-release qualification evidence, rerun the full benchmark suite on the exact
release-candidate commit that will be tagged. Replace the historical table above with the newly measured values and
record all of the following in the environment table:

- the release tag or full repository commit;
- the AsiBackbone package version resolved by that commit;
- CPU, operating system, SDK, runtime, BenchmarkDotNet version, and SQLite mode; and
- the measurement date.

Do not copy forward timing values from an earlier commit when the measured code path, dependency version, or benchmark
job has changed. A scheduled or manually dispatched workflow run may be retained as trend evidence through its
`benchmark-provenance.md` file, but shared-runner timing should not silently replace a release snapshot recorded under a
documented, repeatable environment.

### Current refresh status

A refresh against the AsiBackbone `7.5.0` integration was attempted before the
stable release. BenchmarkDotNet 0.15.8 discovers all five scenarios, but its
default generated-project toolchain fails while rebuilding the Reference
Application project graph because the generated build redirects referenced
project outputs into its temporary artifact tree.

Running the same scenarios with BenchmarkDotNet's in-process toolchain succeeds,
confirming that the benchmark implementations remain executable. Those results
are not substituted for the October 19 snapshot because changing from the
process-isolated toolchain to an in-process toolchain changes the measurement
methodology.

The historical snapshot therefore remains published with its exact dependency
and repository provenance until an equivalent process-isolated measurement can
be recorded.

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
