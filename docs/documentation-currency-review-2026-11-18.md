---
title: "Documentation Currency Review — 2026-11-18"
description: Findings and retained release-history boundaries for ReferenceApplication issue 153.
---

# Documentation Currency Review — 2026-11-18

Tracking issue: [ReferenceApplication #153](https://github.com/AsiBackbone/ReferenceApplication/issues/153).

## Reviewed stable baseline

The published ReferenceApplication release evaluated for this issue is
[1.0.2](https://github.com/AsiBackbone/ReferenceApplication/releases/tag/v1.0.2).
The documentation homepage identifies adopted NCAT `2.11.10` and AsiBackbone `7.5.4`.
These are **current dependency markers**, not replacements for the versions used when an
earlier walkthrough milestone or test fixture was originally produced.

## Documentation inventory and classification

| Documentation surface | Classification | Decision |
| --- | --- | --- |
| `docs/index.md`, `docs/toc.yml` | Current release and navigation | Retain explicit release/dependency discovery at the entry point |
| `docs/walkthrough/index.md`, `docs/walkthrough/toc.yml` | Current navigation for completed integration | Keep links to completed and supplemental procedures |
| `docs/walkthrough/01-*.md` through `08-*.md` | Completed implementation history plus current code guidance | Preserve original scaffold and milestone versions; do not rewrite history to 1.0.2 |
| `docs/walkthrough/02-add-asibackbone-packages.md` | Historical Step 2 plus current dependency inventory | Keep historical `7.2.1` and current `7.5.4` separate; current package list is test guarded |
| `docs/walkthrough/refresh-ncat-baseline.md` | Reproducible scaffold-refresh instructions | Preserve pinned examples, including historical baseline versions |
| `docs/walkthrough/upgrade-between-releases.md` | Current supported upgrade procedure | Clarify stable `1.x` versus earliest tested `v0.1.0-alpha.11` fixture |
| Other `docs/walkthrough/` supplements | Operational, identity, persistence, signing, and deployment guidance | Keep behavior-focused wording unless a version is required for reproducibility |
| `docs/stable-release-policy-v1.md` | Explicitly versioned stable compatibility policy and first-release gates | Preserve its original release-gate history and deliberate SQLite upgrade boundary |
| `docs/release-artifact-signing.md` | Release-artifact provenance and versioned verification evidence | Preserve historical tag-specific incident references |
| `docs/benchmarks/` | Measurement methodology and run-specific evidence | Do not relabel old results as measurements of a newer release |
| `docs/api/`, `docs/docfx.json`, `docs/templates/`, `docs/public/`, `docs/images/` | API landing, site configuration, template and static resources | Regenerate and validate DocFX output from the owning source; no release-line substitution |
| Repository `README.md`, `SECURITY.md`, `CHANGELOG.md`, CI and test fixtures | Current policy, release evidence and owning implementation | Retain historical release records and source-of-truth test fixture identifiers |

The review checked the documentation entry points, walkthrough source and pinned-version
claims against `ReleaseUpgradeEvidence`, `ReleaseUpgradeTests`, `.github/workflows/ci.yml`,
the stable-release policy, and the central dependency-version documentation tests.
It is not a claim that every operational or security assertion has received
independent runtime certification.

## Verified upgrade boundary

- `ReleaseUpgradeEvidence.BoundaryRelease` is `v0.1.0-alpha.11`.
- The release-upgrade CI job uses `PREVIOUS_RELEASE: v0.1.0-alpha.11`.
- `ReleaseUpgradeTests` asserts that CI uses the same boundary and evidence fixture.
- The stable `1.x` policy intentionally retains that boundary until it is deliberately changed.
- This validates the oldest-boundary upgrade path, not every intermediate-version combination.

There is no justification to advance the fixture merely because stable 1.x has shipped.

## Cross-repository context

AsiBackbone Core owns package behavior and its release contract; NCAT owns the
generated scaffold; Learning owns educational examples; the organization site
and `.github` repositories own shared navigation and policy artifacts.
Current versions in those locations are not retroactively substituted for
ReferenceApplication's historical integration snapshots. Changes outside
ReferenceApplication should be tracked in their owning repositories.

## Validation still required

Run the focused upgrade tests and the repository's documentation, link and
release CI checks from the complete checkout. After the documentation workflow
publishes, inspect the rendered upgrade guide and homepage, including links to
the stable release policy and latest published release. Search-index results
are not a substitute for checking the deployed HTML.

The patch does not change application code, persisted schema, migration files,
the oldest supported fixture, or the policy version.
