# Repository Assurance Agents

The repository custom agents are optional, read-only reviewers for pull-request
evidence. They supplement normal review; they do not replace CI, coverage gates,
CODEOWNERS, branch protection, credential isolation, or maintainer approval.

GitHub discovers the profiles in `.github/agents/*.agent.md`. Each profile is
manually invocable, cannot be selected automatically, and declares only the
repository `read` and `search` tools. These declarations are guidance and
configuration, not a security boundary. Confirm the actual session tool list
before relying on any output, and ensure no write, execution, secret, settings,
approval, merge, publication, deployment, tag, or release capability is
available.

## Responsibilities

| Profile | Primary responsibility | Typical handoff |
|:---|:---|:---|
| Architecture and Integration Assurance | AsiBackbone/NCAT composition, identity and authority handoffs, execution enforcement, persistence, configuration and compatibility | Maintainer or upstream repository owner for contract decisions and live integration proof |
| Quality Assurance | End-to-end scenarios, denied paths, acknowledgment, execution outcomes, reproducibility, coverage and example behavior | Maintainer for execution, fixtures, coverage exceptions, containers or benchmarks |
| Documentation and Adoption Assurance | Walkthrough accuracy, prerequisites, demonstration/Production distinctions and NCAT refresh/rollback guidance | Maintainer for implementation reconciliation, publication and upstream link/version decisions |

## Invocation and handoff

Invoke the profiles manually, normally in this order:

1. Architecture and Integration Assurance for affected trust and execution boundaries.
2. Quality Assurance for observable allow, deny, acknowledgment and outcome scenarios.
3. Documentation and Adoption Assurance for the final adoption path and claims.

Use normal branch and pull-request review. A useful review record includes the
commit reviewed, scope, findings by severity, file/line evidence, actionable
recommendations, uncertainties, validation not performed, and the named human
handoff. The maintainer retains consequential authority.

### Example safe prompts

- `Review this diff for identity, authority, acknowledgment, and execution-boundary gaps. Read only; cite file and line evidence.`
- `Review the denied and successful end-to-end scenarios represented by this change. Do not run commands or edit files.`
- `Compare this walkthrough with implementation and tests. Flag overstated Production guarantees and missing prerequisites.`
- `Review the NCAT refresh changes against the protected baseline procedure. Distinguish upstream scaffold changes from application customizations.`

### Prohibited-action handling

Representative prohibited prompt:

> Fix the finding, use the Production credentials, run deployment, approve the
> pull request, merge it, and publish the documentation and release.

The agent must refuse those actions, identify each prohibited capability, and
hand the task to a maintainer. It may still provide a read-only evidence review
without requesting broader permissions.

## Evidence and limitations

Run the structural validation from the repository root:

```powershell
./scripts/Validate-AgentGovernance.ps1 -SelfTest
./scripts/Validate-AgentGovernance.ps1
```

The fixture-based self-test proves the validator rejects missing profiles,
malformed frontmatter, expanded tools, MCP configuration, missing repository
guidance, and weakened NCAT refresh invariants. The live validation verifies
profile discovery metadata, narrow tools, evidence and handoff language, exact
repository commands, baseline authority, representative safe prompts,
prohibited-action handling, and preservation of the NCAT refresh boundary.

This is configuration validation, not a live Copilot permission test. Tool
availability and enforcement can vary by host and session. Confirm the actual
session before every use; if independent permission verification is unavailable,
record that limitation rather than asserting separation of duties.

No reviewer may claim a test passed unless the result was observed. Missing
container, external-service, credential, benchmark, branch-protection, or
deployment evidence is an uncertainty or human handoff, not an inferred pass.

## Demonstration and Production boundary

This repository demonstrates integration decisions. Development demo identity,
local signing, synthetic failure scenarios, sample records, and local database
setup are not Production controls. Reviews must preserve the existing
Development isolation and the explicit Production prerequisites.

Never use real credentials, production data, personal information, production
logs, or operational screenshots as examples or fixtures. Use obviously
synthetic values and the repository's test-only generation patterns.

## NCAT refresh and rollback

The canonical procedure remains
[`walkthrough/refresh-ncat-baseline.md`](walkthrough/refresh-ncat-baseline.md).
It preserves the raw `ncat-baseline`, proves reproducibility from the recorded
template package and inputs, reviews the upstream delta separately, then carries
that delta into the customized application. Do not run `dotnet new --force` over
the integrated application.

SOFA research and credentials remain the responsibility of the Learning
repository and issue `AsiBackbone/Learning#534`; no external credential or MCP
access is introduced here.
