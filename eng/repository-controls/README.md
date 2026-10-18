# Repository controls

These files define the desired GitHub branch rulesets for
`AsiBackbone/ReferenceApplication`. They are portable API request payloads, not
snapshots of server-generated responses, so GitHub-owned fields such as rule
IDs, timestamps, source metadata, and links are intentionally omitted.

## Branch roles

- `main-branch-ruleset.json` protects the ongoing development and integration
  branch. It requires the repository's build, test, documentation, workflow
  security, dependency-review, OWASP Dependency-Check, and CodeQL checks.
- `ncat-baseline-branch-ruleset.json` protects the raw NCAT comparison branch.
  It deliberately does not require ReferenceApplication integration checks.

## Activation sequence

Do not apply new required-check entries to the live `main` ruleset until the
workflow defining each check exists on `main` and has completed successfully.
Requiring a check before GitHub can produce it would block every pull request.

After the workflows land on `main`:

1. Allow the `CI`, `Publish Documentation`, `GitHub Actions security analysis`,
   and `OWASP Dependency-Check software composition analysis` workflows to
   complete successfully on `main`. Confirm the scheduled/manual `OpenSSF
   Scorecard supply-chain security` workflow can publish its results.
2. Apply `main-branch-ruleset.json` through the GitHub repository rulesets API.
3. Read the full live ruleset back through the API and compare its branch
   target, bypass actors, rule types, parameters, and required check names with
   the committed manifest.
4. Confirm a normal pull request can satisfy every required check without a
   ruleset bypass.

The `ncat-baseline` ruleset remains independent so an upstream baseline refresh
does not inherit checks that validate the integrated application.

Dependabot monitors NuGet, Docker, and GitHub Actions dependencies on staggered
weekly schedules. Its pull requests use the same required review, CI, dependency
review, OWASP, CodeQL, and workflow-security checks as other changes to `main`.
