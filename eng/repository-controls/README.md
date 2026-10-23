# Repository controls

These files define the desired GitHub branch rulesets for
`AsiBackbone/ReferenceApplication`. They are portable API request payloads, not
snapshots of server-generated responses, so GitHub-owned fields such as rule
IDs, timestamps, source metadata, and links are intentionally omitted.

## Branch roles

- `main-branch-ruleset.json` protects the ongoing development and integration
  branch. It requires the repository's build, test, container smoke test,
  documentation, workflow security, dependency-review, OWASP Dependency-Check,
  and CodeQL checks. The container smoke test builds the runtime and migration
  images, so a change that breaks the Dockerfile, Compose file, project files,
  or lock files cannot merge. The release upgrade test upgrades a database
  created by the oldest supported release, so a migration that loses or
  changes evidence cannot merge.
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

The ruleset protects `ncat-baseline` from deletion, force pushes, and direct
pushes, but it cannot prove that the branch content is what NCAT generates.
The `NCAT baseline reproducibility` workflow on `main` supplies that evidence:
it regenerates the scaffold from the release named by the baseline's annotated
`ncat-v<version>` tag, with the inputs in `eng/ncat-baseline.json`, and fails on
any material difference. It is deliberately not a required check in either
ruleset. `ncat-baseline` carries no workflows, so a pull request into it cannot
produce the check, and the check does not depend on a pull request into `main`.
Verify a refresh candidate before merging it by running the workflow manually
or the script locally, as
[Refresh the NCAT Baseline](../../docs/walkthrough/refresh-ncat-baseline.md#verify-reproducibility)
describes.

Dependabot monitors NuGet, Docker, and GitHub Actions dependencies on staggered
weekly schedules. Its pull requests use the same required review, CI, dependency
review, OWASP, CodeQL, and workflow-security checks as other changes to `main`.

## OpenSSF Scorecard exceptions

The ReferenceApplication is a reference repository that shows how to integrate
AsiBackbone and the .NET Core Application Template. It is not a distributed
product. The following Scorecard findings are proportionate exceptions. Their
code-scanning alerts are dismissed as "won't fix" with the same reasoning.
Reopen an alert when its condition no longer holds.

| Scorecard check | Reason for the exception | Reconsider when |
| --- | --- | --- |
| CII-Best-Practices | The OpenSSF Best Practices badge belongs to the product repositories, not to a reference repository. | The repository is distributed as a product. |
| Code-Review | The organization has one active maintainer, so no independent approval is available. Both rulesets require pull requests but set `required_approving_review_count` to `0`, matching the organization's policy in [`AsiBackbone/.github` SECURITY.md](https://github.com/AsiBackbone/.github/blob/main/SECURITY.md). | An independent active maintainer is available. |
| Branch-Protection | Required approvers, Code Owner review, and last-push approval cannot be satisfied by a single maintainer, for the same reason. `main` still blocks deletion and force pushes, requires linear history and pull requests, and requires the status checks listed above. | An independent active maintainer is available. |
| SAST, CI-Tests | The only commits and pull requests without CodeQL or CI predate the workflows: the initial NCAT scaffold commit and the first baseline integration (#1, #2). Every later change to `main` has to pass the required CodeQL and build-and-test checks, so these findings fall out of Scorecard's window as new changes land. | A change reaches `main` without those checks. |

The Maintained finding is not dismissed. It reflects only that the repository is
younger than 90 days, and it clears on its own once the repository is older.
