---
title: "Step 4: Define Governance Rules"
description: The planned sample governance rules, where each will be declared, and how each will be tested.
---

# Step 4: Define Governance Rules

**Status:** Planned. This page will list each sample rule, its source location, and its tests when the step lands.

## Planned outcome

A small set of sample governance rules will model realistic decisions. Each rule will be declared in code, registered with the policy evaluator, and tested for both allow and deny outcomes.

## Planned procedure

1. Keep rules in a dedicated folder so they are easy to find and read as examples.
2. Give every rule a stable policy version, policy hash, and reason codes, so decision receipts remain comparable across releases.
3. Cover a range of decision shapes, for example:
   - an allow-or-deny rule driven by the actor's role;
   - a rule that requires an explicit acknowledgment before a consequential operation proceeds; and
   - a rule that requires a capability grant.
4. Test each rule in isolation, then through the evaluator.

When the step lands, a table here will name each rule, the decision it governs, its outcomes and reason codes, and its source file.

## Authoritative references

- [AsiBackbone Custom Decision Policy Examples](https://asibackbone.github.io/AsiBackbone/articles/custom-decision-policy-examples.html)
- [AsiBackbone Core Domain Language](https://asibackbone.github.io/AsiBackbone/articles/core-domain-language.html)
- [AsiBackbone Glossary](https://asibackbone.github.io/AsiBackbone/articles/glossary.html)

## Next

[Step 5: Govern endpoints](05-govern-endpoints.md)
