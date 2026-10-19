---
title: "Step 8: Benchmark governance overhead"
description: Measure the Reference Application host's governance policy and evidence-persistence overhead with repeatable BenchmarkDotNet scenarios.
---

# Step 8: Benchmark governance overhead

**Status:** Complete. The repository contains repeatable BenchmarkDotNet scenarios for the host-owned policy and
evidence-persistence paths, a recorded reference snapshot, and a scheduled workflow that preserves complete results.

Step 7 makes the governed application observable. Step 8 measures selected integration costs without confusing the
interactive explorer's transient feedback with benchmark evidence. The benchmarks use the production dependency
injection registrations, policy composition, evidence mapping, and persistence adapters owned by this host.

## Run the measurements

Run every scenario from the repository root in Release configuration:

```powershell
dotnet run -c Release --project tests/ReferenceApplication.Benchmarks -- --filter "*"
```

BenchmarkDotNet reports the mean, statistical error, standard deviation, and allocated bytes for each scenario. The
`Governance Benchmarks` workflow also runs weekly and on demand, then uploads the full `BenchmarkDotNet.Artifacts`
directory. It deliberately does not run on pull requests or ordinary pushes: timing from a shared runner is useful for
trend evidence, but is not a stable merge gate.

## What is measured

The policy scenarios measure the host's production composed evaluator for the administrator-and-consequential and
capability-scope rule sets. The persistence scenarios measure three host-owned paths:

- mapping, hashing, and appending a decision receipt to the SQLite audit ledger;
- mapping and delivering an NCAT audit completion as an AsiBackbone lifecycle event; and
- detecting and acknowledging an idempotent duplicate completion.

Schema migration and fixture setup happen outside the measured operations. Persistence scenarios use a temporary
file-backed SQLite database in write-ahead logging mode with connection pooling disabled.

## Reference results

This snapshot was recorded on October 19, 2026, using an AMD Ryzen 5 7430U, Windows 11 25H2, .NET SDK 10.0.402,
.NET 10.0.13, BenchmarkDotNet 0.15.8, and the SQLite configuration described above.

| Scenario | Mean | Error (99.9% CI half-width) | Allocated per operation |
| --- | ---: | ---: | ---: |
| `host_policy.administrator_and_consequential` | 506.6 ns | 26.06 ns | 1,640 B |
| `host_policy.capability_scope` | 234.2 ns | 6.59 ns | 320 B |
| `host_evidence.receipt_to_sqlite_ledger` | 6.679 ms | 0.343 ms | 163.95 KB |
| `host_evidence.audit_completion_delivered` | 10.757 ms | 0.951 ms | 136.79 KB |
| `host_evidence.audit_completion_duplicate` | 4.655 ms | 0.530 ms | 114.16 KB |

These values are a reproducibility record, not a target or service-level objective. See the detailed
[governance-overhead benchmark record](../benchmarks/governance-overhead.md) for job settings, complete environment
metadata, and interpretation guidance.

## Measurement boundary

The Reference Application benchmarks intentionally do **not** measure framework-only hot paths, HTTP transport,
authentication, antiforgery, serialization, security headers, rate limiting, browser rendering, server saturation,
or production-database behavior. They therefore do not predict end-to-end production latency.

Framework-level measurements belong to AsiBackbone's
[performance benchmark baseline](https://asibackbone.github.io/AsiBackbone/articles/performance-benchmark-baseline.html).
Keeping the two scopes separate makes regressions actionable: the framework baseline measures AsiBackbone itself,
while this step measures the surrounding integration owned by the Reference Application host.

Compare results only across equivalent hardware, operating systems, runtimes, repository revisions, database modes,
and BenchmarkDotNet settings. Prefer several repeated runs before acting on a small difference.
