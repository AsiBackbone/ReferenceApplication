---
title: Integration Walkthrough
description: The ordered, in-progress procedure for turning an NCAT-generated ASP.NET Core application into a governed AsiBackbone host with sample governance rules.
---

# Integration Walkthrough

This walkthrough records the procedure being used to build the Reference Application: start from an unmodified NetCoreApplicationTemplate (NCAT) scaffold, then adopt AsiBackbone one step at a time until the host evaluates sample governance rules and records audit evidence.

Each step names what changed, where it changed, and which authoritative article defines the behavior being used.

## What this walkthrough is, and is not

- It **is** a record of the steps taken in this repository, in order, tied to the code they produced.
- It **is not** a tutorial on governance, ASP.NET Core, or architecture. For that, use [AsiBackbone Learning](https://asibackbone.github.io/Learning/), which remains the authoritative training site.
- It **does not** redefine framework or template behavior. When a step uses an AsiBackbone or NCAT feature, the [AsiBackbone](https://asibackbone.github.io/AsiBackbone/) and [NCAT](https://asibackbone.github.io/NetCoreApplicationTemplate/) sites are authoritative; this site links to them instead of repeating them.

## Steps

| Step | Outcome | Status |
|:---|:---|:---|
| [1. Scaffold from NCAT](01-scaffold-from-ncat.md) | An unmodified NCAT application, preserved on the `ncat-baseline` branch | Complete |
| [2. Add AsiBackbone packages](02-add-asibackbone-packages.md) | The host references the AsiBackbone packages it needs, with locked versions | Complete |
| [3. Register AsiBackbone services](03-register-asibackbone-services.md) | AsiBackbone is composed into the NCAT startup path | Complete |
| [4. Define governance rules](04-define-governance-rules.md) | Sample governance rules the host evaluates | Complete |
| [5. Govern endpoints](05-govern-endpoints.md) | Consequential endpoints run through governance before executing | Complete |
| [6. Persist and verify audit evidence](06-persist-and-verify-audit.md) | Decision evidence persisted through the host-owned `DbContext` and correlated with NCAT mutation audit | Complete |
| [7. Observe governance in action](07-observe-governance.md) | An interactive explorer over the real governance and evidence pipeline | In progress |

Steps marked **Planned** describe the intended procedure. Each page is updated with the concrete code and commands when that step lands in the repository.

## Supplemental procedures

These procedures support the walkthrough without adding new AsiBackbone integration steps:

- After Step 1, use [Refresh the NCAT baseline](refresh-ncat-baseline.md) whenever the application adopts a newer
  template release. It isolates the raw generated change on `ncat-baseline` before reconciling that same delta with
  the integrated application.
- After Step 3 and before exercising database-dependent integration scenarios, follow
  [Create the development database](local-database-setup.md). It applies the inherited NCAT migration history because
  the application deliberately does not create or migrate databases at startup.
- After Step 5, use the [Development-only demo identity controls](development-demo-identity.md) to exercise allowed,
  denied, and acknowledgment-required outcomes without an external identity provider. The reference configuration
  enables the control only in `Development`; it cannot be registered in another environment.

## Test inheritance rule used throughout

The Reference Application keeps the NCAT-generated test suite as an inherited regression contract while the host evolves. The walkthrough follows four rules:

1. **Keep inherited NCAT tests when the underlying behavior still applies.** Integration work should not discard baseline coverage simply because the application has started to diverge from the scaffold.
2. **Modify or remove an inherited test only when ReferenceApplication intentionally changes the assumption that test exercised.** The reason for the divergence should be documented close to the affected code or test.
3. **Add new tests for ReferenceApplication-specific behavior.** As AsiBackbone integration lands, governance decisions, acknowledgments, capability boundaries, audit evidence, and other host-owned behavior should gain coverage alongside the inherited NCAT tests.
4. **Run the complete suite after every walkthrough step.** New tests do not replace the inherited suite; both are used together to detect unintended structural or behavioral changes.

This distinction is also useful when a newer NCAT baseline is integrated later. Failures in inherited tests can expose an upstream behavior change, an intentional ReferenceApplication divergence, or a real regression that must be reconciled during the three-way merge.

## Ownership rule used throughout

The Reference Application follows the one-way dependency rule that both projects document:

```text
ReferenceApplication (host)
  -> owns the NCAT-generated code (it belongs to this repository after generation)
  -> will reference AsiBackbone packages as the integration steps land

AsiBackbone and NCAT
  -> do not reference ReferenceApplication or each other
```

The host owns its database provider, connection strings, migrations, authentication, and deployment. As the integration lands, AsiBackbone will contribute governance services, model configuration, and storage helpers. See [AsiBackbone Integration Boundaries](https://asibackbone.github.io/AsiBackbone/articles/integration-boundaries.html) and [NCAT Audit Accountability Integration](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/audit-accountability-integration.html).
