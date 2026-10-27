---
title: "Step 1: Scaffold from NCAT"
description: How the Reference Application was generated from the netcoreapp-template package and how the unmodified scaffold is preserved for comparison.
---

# Step 1: Scaffold from NCAT

**Status:** Complete. Released in [`v0.1.0-alpha.1`](https://github.com/AsiBackbone/ReferenceApplication/releases/tag/v0.1.0-alpha.1).

## Outcome

An unmodified NCAT-generated ASP.NET Core application named `ReferenceApplication`, preserved on its own protected branch so every later change can be compared against the scaffold.

## Procedure

Run every command from PowerShell. Do not open the generated folder in Visual Studio until step 3 is complete: creating, saving, or cleaning up files in Visual Studio can change the scaffold before it is committed.

1. Install the exact template version. [NCAT Template Packaging](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/template-packaging.html) has the authoritative install instructions. The Reference Application was generated from NCAT 2.11.4, and this step records that original generation. The baseline has since been refreshed to NCAT 2.11.7 with [Refresh the NCAT Baseline](refresh-ncat-baseline.md):

   ```powershell
   dotnet new uninstall NetCoreApplicationTemplate
   dotnet new install NetCoreApplicationTemplate@2.11.4
   ```

   The `uninstall` command reports an error if the package is not installed; that is expected. Pinning the version matters: an unversioned install keeps whichever version is already installed or picks the latest release.

2. Generate the application with the default options. The defaults select cookie authentication with authenticated-by-default routed endpoints and SQLite for local development:

   ```powershell
   dotnet new netcoreapp-template --name ReferenceApplication --skipRestore true
   ```

   `--skipRestore true` prevents the template's post-create restore from running before the scaffold is committed, so the generated `packages.lock.json` files are committed exactly as the template produced them. [Refresh the NCAT baseline](refresh-ncat-baseline.md) generates scaffolds the same way.

   Before committing, confirm the scaffold came from the intended template version:

   ```powershell
   Select-String .\ReferenceApplication\Directory.Build.props -Pattern '<VersionPrefix>'
   ```

   The output must show `<VersionPrefix>2.11.4</VersionPrefix>`. If it shows another version, the wrong template package is installed: delete the generated folder, repeat step 1, and check the installed version with `dotnet new uninstall`, which lists installed packages and their versions when run without arguments.

3. Initialize the local repository and commit the scaffold unchanged, then create the `ncat-baseline` branch and tag it:

   ```powershell
   Set-Location .\ReferenceApplication
   git init --initial-branch main
   git add --all
   git commit -m "NCAT Scaffold 2.11.4"
   git branch ncat-baseline
   git tag -a ncat-v2.11.4 ncat-baseline -m "Raw NetCoreApplicationTemplate 2.11.4 scaffold"
   ```

4. Build and test the scaffold before changing anything, so later failures can be attributed to integration work rather than to the baseline:

   ```powershell
   dotnet restore ReferenceApplication.slnx --locked-mode
   dotnet build ReferenceApplication.slnx --configuration Release --no-restore
   dotnet test --solution ReferenceApplication.slnx --configuration Release --no-build
   git status --short
   ```

   `git status --short` must print nothing. Build output is ignored by the generated `.gitignore`, so any listed file means the restore or build changed the scaffold; investigate before pushing.

5. Link the local repository to the GitHub repository, then push both branches and the tag:

   ```powershell
   git remote add origin https://github.com/<remote repository>
   git push -u origin main
   git push -u origin ncat-baseline
   git push origin ncat-v2.11.4
   ```

6. Protect the baseline. The desired branch rulesets for `main` and `ncat-baseline` are versioned under `eng/repository-controls/`, so repository settings are reviewable alongside the code.

7. Keep the baseline untouched: make integration changes on short-lived working branches and merge them into `main` through pull requests, never into `ncat-baseline`.

The adopted baseline and generation inputs are recorded for machines as well as readers: `eng/ncat-baseline.json` holds the current version, template package, short name, application name, and options (the defaults above, passed explicitly), and the annotated `ncat-v<version>` tag on the baseline independently confirms the release. The [reproducibility check](refresh-ncat-baseline.md#verify-reproducibility) regenerates the scaffold from both and fails if the tag, manifest, or `ncat-baseline` content has drifted.

## Resulting layout

| Project | Role |
|:---|:---|
| `src/ReferenceApplication.Web` | ASP.NET Core host: startup, middleware pipeline, authentication, controllers, Razor Pages, health checks |
| `src/ReferenceApplication.Infrastructure` | EF Core `ApplicationDbContext`, save-changes pipeline, and NCAT mutation audit |
| `tests/ReferenceApplication.Web.Tests` | Baseline tests generated by the template |

[NCAT Project Structure](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/project-structure.html) is the authoritative description of each generated area.

## Preserve the NCAT tests

The generated test project remains part of ReferenceApplication after the scaffold step. It is intentionally carried forward as regression coverage for the NCAT behaviors the host still relies on.

When ReferenceApplication changes structure deliberately, an inherited test may reveal an assumption that no longer applies. That is a signal to evaluate the change, not a reason to discard the baseline suite. The rule is:

- keep an inherited NCAT test when its behavior still applies;
- modify or remove it only when ReferenceApplication intentionally changes the assumption it exercised, and document that divergence;
- add new tests for behavior introduced by ReferenceApplication and AsiBackbone integration; and
- run the full inherited-plus-application test suite after each walkthrough step.

For example, NCAT serves `/` from a Razor Page. If ReferenceApplication moves the landing page to `HomeController.Index`, the Razor Page-specific authorization test no longer represents the host. That test coverage is then adjusted while the underlying authorization and rate-limiting behaviors remain covered.

## Why preserve the baseline

Keeping the untouched scaffold on `ncat-baseline` means the full cost of adopting AsiBackbone is always visible as a diff:

```powershell
git diff ncat-baseline main
```

## Supplemental: Refresh to a newer NCAT release

ReferenceApplication owns its generated code once it has been scaffolded, so a new NCAT release
never reaches the application on its own. Installing a newer template package changes nothing in
this repository. Adopting a release is a deliberate, reviewed change, and
[Refresh the NCAT Baseline](refresh-ncat-baseline.md) is the procedure to follow.

Use it whenever ReferenceApplication should move to a newer NetCoreApplicationTemplate version, for
example to pick up dependency updates, analyzer rules, or fixes published in a template release.

In outline, the procedure:

1. Generates fresh scaffolds from the current and the new template version, using the same
   application name and options recorded in this step.
2. Confirms that `ncat-baseline` still matches the current version's scaffold exactly.
3. Stages only the difference between the two releases onto `ncat-baseline`, through a pull request
   that contains nothing but generated output.
4. Tags the merged baseline as `ncat-v<version>`, so every refresh has a known starting point.
5. Carries that same commit into the integrated application through a second, separate pull request
   to `main`, where any conflicts with ReferenceApplication's own changes are resolved.

Keeping the two pull requests separate is what lets reviewers tell an upstream NCAT change apart from
a ReferenceApplication decision. Do not regenerate the application in place with
`dotnet new --force`: it can overwrite intentional changes and leaves no clear record of what the
release actually changed.

Before starting, have the current baseline tag (currently `ncat-v2.11.7`) and the target release
number to hand.

## Next

[Step 2: Add AsiBackbone packages](02-add-asibackbone-packages.md)
