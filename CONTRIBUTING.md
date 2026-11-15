# Contributing to the Reference Application

Thank you for helping improve the AsiBackbone Reference Application.

The Reference Application is a stable integration example, not a product. It shows how an ASP.NET Core host generated
from the [.NET Core Application Template (NCAT)](https://github.com/AsiBackbone/NetCoreApplicationTemplate) adopts
[AsiBackbone](https://github.com/AsiBackbone/AsiBackbone) governance, and it documents that integration step by step in
the [Integration Walkthrough](https://asibackbone.github.io/ReferenceApplication/walkthrough/). Contributions should keep
the example clear, runnable, and honest about what it does and does not show.

This guide adds what is specific to this repository. The
[AsiBackbone organization contributing guide](https://github.com/AsiBackbone/.github/blob/main/CONTRIBUTING.md) is the
default for everything else, including the documentation ownership rules. Participation is subject to the
[AsiBackbone Code of Conduct](https://github.com/AsiBackbone/.github/blob/main/CODE_OF_CONDUCT.md).

## Does it belong here?

This host combines code from three repositories. Contribute to the one that owns the behavior you want to change. The
[Security Policy](SECURITY.md#where-to-report) draws the same boundary for vulnerability reports.

| The change is to | Contribute to |
| --- | --- |
| An AsiBackbone package, API, analyzer, or runtime behavior that reproduces without this host's own code | [AsiBackbone](https://github.com/AsiBackbone/AsiBackbone/blob/main/CONTRIBUTING.md) |
| NCAT's generated scaffold or template defaults, reproducible in a fresh `dotnet new netcoreapp-template` scaffold with the same options | [NetCoreApplicationTemplate](https://github.com/AsiBackbone/NetCoreApplicationTemplate/blob/main/CONTRIBUTING.md) |
| General architecture concepts, tutorials, or labs that are useful beyond this host | [Learning](https://github.com/AsiBackbone/Learning/blob/main/CONTRIBUTING.md) |
| Anything this repository adds on top of the NCAT scaffold: the host's governance integration, sample rules, explorer and Development sandbox, configuration, container and Compose files, workflows, walkthrough, and benchmarks | This repository |

Everything this repository adds is visible with `git diff ncat-baseline main`. If a change would only touch files that
are identical to the NCAT scaffold, it probably belongs in NCAT and reaches this host through a baseline refresh.

When you are unsure, ask in [Q&A](https://github.com/orgs/AsiBackbone/discussions/categories/q-a) and name
`AsiBackbone/ReferenceApplication` in the post.

## Before you start

- Report a reproducible host defect, documentation defect, or bounded demonstration request with the
  [issue forms](https://github.com/AsiBackbone/ReferenceApplication/issues/new/choose).
- Discuss open-ended proposals first, in [Ideas](https://github.com/orgs/AsiBackbone/discussions/categories/ideas) or
  [Architecture & Integration](https://github.com/orgs/AsiBackbone/discussions/categories/architecture-integration).
- Never put a suspected vulnerability, secret, token, key, or personal data in a public Issue, pull request, or
  Discussion. Report it privately as the [Security Policy](SECURITY.md) describes.

## Branches

| Branch | What it accepts |
| --- | --- |
| `main` | All Reference Application changes, through pull requests from short-lived working branches. |
| `ncat-baseline` | Only the raw, unmodified scaffold generated from a selected NCAT release. |

Name working branches for the task and include the issue number where there is one, for example
`docs/issue-123-short-description` or `fix/issue-123-short-description`.

Never commit host changes to `ncat-baseline`. It exists so `git diff ncat-baseline main` shows exactly what this host
adds, and the weekly *NCAT baseline reproducibility* workflow fails if the branch drifts from what its recorded NCAT
release generates. Adopting a newer NCAT release takes two pull requests: one into `ncat-baseline` with only the
regenerated scaffold, then a separate integration change into `main`. Follow
[Refresh the NCAT Baseline](https://asibackbone.github.io/ReferenceApplication/walkthrough/refresh-ncat-baseline.html)
and run `./scripts/Test-NcatBaselineReproducibility.ps1` before opening the baseline pull request.

## Build and test

You need the .NET SDK selected by `global.json`, and Docker only for the Compose checks.

```powershell
dotnet restore --locked-mode
dotnet build --configuration Release
dotnet format --verify-no-changes --verbosity minimal
dotnet test --configuration Release
```

CI also gates code coverage. To run the same gate locally, follow
[Restore, build, and test](README.md#restore-build-and-test) in the README.

After an intentional dependency change, run `dotnet restore --force-evaluate`, review the lock-file changes, and commit
them with the change. Package versions are managed centrally in `Directory.Packages.props`.

For a change to the Dockerfile, Compose files, or migrations, also run `docker compose up --build` and check the health
endpoints, as described in [Run with Docker Compose](README.md#run-with-docker-compose).

## Documentation

The DocFX site under `docs/` holds the API reference, the walkthrough, and the benchmark results. For a documentation
change, build the site with warnings treated as errors and validate it:

```powershell
dotnet tool restore
dotnet build ReferenceApplication.slnx --configuration Release
dotnet tool run docfx -- docs/docfx.json --warningsAsErrors
./scripts/Validate-LandingPage.ps1 -SiteDirectory ./docs/_site
```

When you edit the walkthrough:

- keep each step's historical status lines as history, and keep current-version statements consistent with
  `Directory.Packages.props` and `eng/ncat-baseline.json`;
- describe AsiBackbone or NCAT behavior by linking to the owning project's documentation rather than restating it; and
- keep sample governance rules framed as illustrations, not recommendations for any specific domain.

## Pull requests

Keep each pull request focused on one issue or purpose. In the description, include:

- the problem, and `Closes #<number>` when the pull request completes an issue;
- what changed and what deliberately did not change;
- how you validated it, with the commands you ran; and
- any effect on the stable compatibility surface, security boundaries, Development-only isolation, database
  migrations, or documentation.

Add a line under `## Unreleased` in [CHANGELOG.md](CHANGELOG.md) for any change a reader or operator would notice.

Changes to the stable HTTP surface, reason codes, persisted evidence, or migrations must follow the
[Stable Release Policy v1](docs/stable-release-policy-v1.md). Anything Development-only must stay registered only when
the environment is exactly `Development`, with tests that prove it is absent elsewhere.

Pull requests into `main` must pass the required checks recorded in
[`eng/repository-controls/main-branch-ruleset.json`](eng/repository-controls/main-branch-ruleset.json), including
build and test, the container smoke test, the release upgrade test, the documentation build, workflow security,
dependency review, OWASP Dependency-Check, and CodeQL. Passing checks are evidence, not a substitute for reviewing the
final diff.

## Licensing

Contributions are accepted under the [MIT License](LICENSE.txt) that covers this repository. Record the source and
license of any third-party asset in [ASSETS-LICENSES.md](ASSETS-LICENSES.md).
