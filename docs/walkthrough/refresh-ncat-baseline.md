---
title: "Supplemental: Refresh NCAT Baseline"
description: How to isolate a new NCAT scaffold release, update the protected raw baseline, and carry the same delta into the integrated application.
---

# Supplemental: Refresh NCAT Baseline

**Status:** Available after Step 1 whenever ReferenceApplication adopts a newer NCAT release.

ReferenceApplication owns its generated source after scaffolding, so installing a newer
NetCoreApplicationTemplate (NCAT) package does not update the application automatically. This
procedure regenerates the old and new template versions with identical inputs, verifies that the
protected `ncat-baseline` branch still matches the old scaffold exactly, stages only the upstream
difference onto that branch, and then carries the same delta into the integrated application.

An automated [reproducibility check](#verify-reproducibility) proves, before and after each refresh, that
`ncat-baseline` is exactly what its recorded NCAT release generates.

The commands below preserve the historical `2.11.4` to `2.11.5` refresh as a concrete example. Every version
number is held in two variables, so a later upgrade only changes those two lines. The application
name and template options must stay identical to those recorded in
[Step 1: Scaffold from NCAT](01-scaffold-from-ncat.md).

## Why this approach

Comparing two clean generated scaffolds separates an NCAT release change from everything
ReferenceApplication has added since generation. That separation matters because it:

- prevents AsiBackbone integration and other host-owned changes from being mistaken for
  template changes;
- captures additions, modifications, and deletions from the template rather than relying on an
  in-place overwrite;
- gives the `ncat-baseline` pull request a small, auditable raw-scaffold diff; and
- lets the integration pull request reconcile that exact baseline commit with the customized
  application through Git's normal conflict handling.

Do not run `dotnet new --force` over the integrated branch. It can overwrite intentional
ReferenceApplication changes and does not provide the same clear record of what changed between
template releases.

### Why the delta is staged by mirroring, not by applying a patch

Producing the delta with `git diff --no-index` and applying it with `git apply --index -p2` is
fragile in this repository. The `.gitattributes` that NCAT generates checks out many file types
(`*.json`, `*.props`, `.editorconfig`, `*.cs`, and others) with CRLF line endings but stores them
in the index with LF. When the generated files carry CRLF, a patch built from them carries CRLF
context lines, so `git apply --check` passes against the CRLF working tree but
`git apply --index` fails against the LF index. `--ignore-whitespace` appears to fix that, but it
writes CRLF content into the index.

Mirroring each scaffold into the baseline worktree and staging it avoids the problem entirely:
Git normalizes every file through the repository's own `.gitattributes`, deletions and additions
are detected automatically, and no path-prefix (`-p`) handling is involved.

## Verify reproducibility

`scripts/Test-NcatBaselineReproducibility.ps1` turns the baseline's provenance into evidence a reviewer can rerun.
The *NCAT baseline reproducibility* workflow runs it weekly, on pull requests into `main` that change the check, and
on demand.

The baseline records its provenance in two places, neither of which is a file on `ncat-baseline` itself:

- **The release.** The annotated `ncat-v<version>` tag on the baseline commit names the NCAT release that generated it.
- **The adopted version and generation inputs.** `eng/ncat-baseline.json` on `main` records the current NCAT version,
  template package, short name, application name, and every template option. The version must agree with the tag,
  while the options are passed explicitly so a later template-default change cannot silently change the inputs.

The check then:

1. resolves the baseline commit, `origin/ncat-baseline` by default, and verifies its tag against the manifest version;
2. installs exactly that `NetCoreApplicationTemplate` release from NuGet.org into an isolated, temporary template
   hive, leaving the templates installed on the machine untouched;
3. generates the scaffold twice and fails if the two generations differ in any byte;
4. checks that the generated `Directory.Build.props` records the same NCAT version;
5. mirrors the scaffold onto a detached worktree of the baseline commit and stages it, so Git normalizes line endings
   through the baseline's own `.gitattributes`, exactly as the refresh does; and
6. fails, printing the staged diff, when any file is added, removed, or changed.

Nothing generated is normalized or ignored by the check itself, and none needs to be: NCAT generation has been
deterministic, and step 3 proves it on every run. Two kinds of difference are reported but are not failures, because
the baseline's own files define them as non-material:

- **Line endings only.** A file whose bytes differ only in line endings that the baseline's `.gitattributes`
  normalizes. NCAT's `.gitattributes` checks many file types out with CRLF but stores them with LF.
- **Ignored.** A generated file excluded by the baseline's `.gitignore`, which the raw scaffold commit could never have
  contained.

If NCAT ever generates nondeterministic content, the check fails at step 3 and names the files. Handle that by adding a
narrow, documented rule for those specific files to the script and to this section, never by ignoring files broadly.

Run it from the root of a `main` checkout with PowerShell 7.2 or later, after fetching the baseline and tags:

```powershell
git fetch origin ncat-baseline --tags
./scripts/Test-NcatBaselineReproducibility.ps1
```

A passing run ends with evidence like this:

```text
NCAT baseline reproducibility
  Ref:               origin/ncat-baseline
  Commit:            <baseline commit SHA>
  NCAT release:      NetCoreApplicationTemplate@2.11.5 (tag ncat-v2.11.5)
  Generation:        dotnet new netcoreapp-template --name ReferenceApplication --authProvider cookie --dbProvider sqlite --skipRestore true
  Generated files:   <count>; two generations byte-identical
  Line endings only: <count> file(s), normalized by the baseline's .gitattributes
  Ignored:           <count> generated file(s) excluded by the baseline's .gitignore
  Material changes:  0
Result: origin/ncat-baseline at <short SHA> is reproducible from NetCoreApplicationTemplate@2.11.5.
```

In the workflow, the same evidence is written to the run's job summary. `-SelfTest` exercises the tag, determinism,
and comparison logic against a synthetic repository without the .NET SDK; the workflow runs it first.

A failure means the baseline is not what the recorded release generates. Typical causes are a manual edit to
`ncat-baseline`, a tag on the wrong commit, or a scaffold generated with other options. Do not fix it by editing
`ncat-baseline` by hand: restore it to the regenerated output through a pull request, or correct the tag.

## Prerequisites

- A clean clone of ReferenceApplication with `origin` fetched, including tags.
- The .NET SDK required by `global.json`.
- Windows PowerShell 5.1 or PowerShell 7. The commands use `robocopy`, which ships with Windows.
- The current baseline commit is tagged `ncat-v<version>`. The initial scaffold is tagged
  `ncat-v2.11.4`.
- PowerShell 7.2 or later for the [reproducibility check](#verify-reproducibility).

The install commands change the locally installed version of the template package; the
procedure leaves the newer version installed.

## 1. Set the versions and generate the scaffolds

From the directory that will hold the scratch folder (outside the repository), set the two
versions once, and name the two short-lived working branches the refresh uses: one for the
pull request into `ncat-baseline` and one for the pull request into `main`. Choose names that suit
your task; this guide does not prescribe them. The remaining steps rely on these variables, so run
them in the same PowerShell session, or set them again after opening a new one.

```powershell
$Old = '2.11.4'
$New = '2.11.5'
$BaselineBranch = Read-Host 'Short-lived branch for the ncat-baseline pull request'
$IntegrationBranch = Read-Host 'Short-lived branch for the main pull request'
$Scratch = Join-Path (Get-Location) "ncat-refresh-$($New -replace '\.', '')"

New-Item -ItemType Directory -Path $Scratch | Out-Null
Set-Location $Scratch
```

Generate the old scaffold, capturing the template's option list:

```powershell
dotnet new uninstall NetCoreApplicationTemplate
dotnet new install "NetCoreApplicationTemplate@$Old"
dotnet new netcoreapp-template --help > "options-$Old.txt"
dotnet new netcoreapp-template --name ReferenceApplication --output old --skipRestore true
```

The `uninstall` command reports an error if the package is not currently installed; that is
expected and harmless.

Generate the new scaffold twice: once as `new`, and once as `new-repeat` to prove that the
template's output is deterministic.

```powershell
dotnet new uninstall NetCoreApplicationTemplate
dotnet new install "NetCoreApplicationTemplate@$New"
dotnet new netcoreapp-template --help > "options-$New.txt"
dotnet new netcoreapp-template --name ReferenceApplication --output new --skipRestore true
dotnet new netcoreapp-template --name ReferenceApplication --output new-repeat --skipRestore true
```

### Check the template options

```powershell
git diff --no-index --no-color -- "options-$Old.txt" "options-$New.txt"
```

Both scaffolds must be generated with the options recorded in Step 1, which are currently the
template defaults (cookie authentication, authenticated-by-default routed endpoints, and SQLite).
If the two option lists match, continue.

If the new release adds an option or changes a default value, the default scaffold for `$New` is
no longer generated with the same inputs as the one for `$Old`. Decide which value
ReferenceApplication should use, then regenerate both `old` and `new` passing that option
explicitly, so the delta contains only the release change. Add the option to Step 1 and to
`version` and `options` in `eng/ncat-baseline.json` so later refreshes, current-version documentation checks, and the
reproducibility check use the same adopted baseline. Until that `main` change merges, pass the updated manifest to the
check with `-ManifestPath`.

### Check that generation is deterministic

```powershell
git diff --no-index --stat -- new new-repeat
```

This must print nothing (exit code `0`). Any difference means the template generates
nondeterministic content, such as GUIDs, random ports, or timestamps. That content would appear
as noise in every refresh. Stop and decide how to handle those files before continuing.

## 2. Prepare the baseline worktree

From the local ReferenceApplication repository root, on an up-to-date `main`, record the root,
confirm that the remote baseline is still exactly the tagged `$Old` scaffold and reproducible from
it, then create a worktree for the refresh:

```powershell
git fetch origin --tags
$RepositoryRoot = (Get-Location).Path

./scripts/Test-NcatBaselineReproducibility.ps1

$tagCommit = git rev-list -n 1 "ncat-v$Old"
$baselineCommit = git rev-list -n 1 origin/ncat-baseline

if (-not $tagCommit) {
  throw "Tag ncat-v$Old was not found. Fetch tags or create the tag before continuing."
}
if ($tagCommit -ne $baselineCommit) {
  throw "origin/ncat-baseline has moved since ncat-v$Old. Investigate before continuing."
}

git worktree add "..\ReferenceApplication-ncat-$New" -b $BaselineBranch origin/ncat-baseline

Set-Location "..\ReferenceApplication-ncat-$New"
```

The commands contain no placeholders: `$Old`, `$New`, and `$BaselineBranch` come from step 1, and
`git rev-list -n 1` resolves the annotated tag to the commit it points at. The reproducibility check
reads `$Old` from the tag itself; if it fails, stop, because the delta would otherwise build on a
baseline that its release does not reproduce.

Define the helper that replaces the worktree contents with a scaffold and stages the result.
`robocopy /MIR` copies new and changed files and deletes files the scaffold no longer contains,
while `/XD .git /XF .git` protects the worktree's Git link. `git add --renormalize .` forces Git
to rehash every tracked file, so a same-size change cannot be missed through cached file
timestamps.

```powershell
function Sync-Scaffold {
  param([Parameter(Mandatory)][string] $Source)

  robocopy $Source . /MIR /XD .git /XF .git /NFL /NDL /NJH /NJS /NP | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }

  git add --all
  git add --renormalize .
}
```

`robocopy` exit codes below `8` indicate success. Git may print a "LF will be replaced by CRLF"
warning for each generated file whose line endings differ from the checkout rules; these warnings
are expected and do not indicate a content change.

### Verify the baseline matches the old scaffold

```powershell
Sync-Scaffold "$Scratch\old"

git diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
  git diff --cached --stat
  throw "ncat-baseline does not match a fresh NCAT $Old scaffold. Investigate before continuing."
}
```

Nothing must be staged. Because staging normalizes line endings, this check reports real content
drift only: a file that was edited, added, or removed on `ncat-baseline` after generation, or a
scaffold that was generated with different inputs. If it fails, do not continue; the delta would
otherwise include that drift.

## 3. Stage and review the new scaffold

```powershell
Sync-Scaffold "$Scratch\new"
```

Review exactly what was staged:

```powershell
git diff --cached --check
git diff --cached --stat
git diff --cached
```

For 2.11.5, expect the generated delta to be narrowly limited to:

- `.editorconfig`
- `Directory.Packages.props`
- the three `packages.lock.json` files

If unrelated application, documentation, workflow, or AsiBackbone integration files appear, stop
and investigate. If `.gitattributes` itself is in the delta, review it with extra care: it
changes how every other file is normalized.

Optionally keep the reviewed delta as a patch file to attach to the pull request:

```powershell
git diff --cached --binary --output="$Scratch\ncat-$Old-to-$New.patch"
```

Validate the refreshed raw scaffold:

```powershell
dotnet restore ReferenceApplication.slnx --locked-mode
dotnet build ReferenceApplication.slnx --configuration Release --no-restore
dotnet format ReferenceApplication.slnx --verify-no-changes --no-restore
dotnet test --solution ReferenceApplication.slnx --configuration Release --no-build
```

`dotnet test --solution` relies on the Microsoft.Testing.Platform runner selected in
`global.json`; keep that setting if the command is copied elsewhere.

Commit and push the branch:

```powershell
git commit -m "chore: refresh NCAT baseline to $New"
git push -u origin $BaselineBranch
```

The pushed branch is not tagged yet, so verify it as a candidate for `$New`. The check runs from the
`main` checkout, because the baseline worktree contains only generated files:

```powershell
& "$RepositoryRoot\scripts\Test-NcatBaselineReproducibility.ps1" -Ref "origin/$BaselineBranch" -TemplateVersion $New
```

Alternatively, run the *NCAT baseline reproducibility* workflow manually with `ref` set to
`origin/<branch>` and `template_version` set to the new version.

Open a pull request targeting `ncat-baseline`, not `main`. Include the check's evidence, or a link
to the workflow run, in the pull request description.

## 4. Tag the new baseline

Once the baseline pull request is squash-merged, the resulting commit is itself the exact raw
generated delta. From the repository root, tag it so the next refresh knows its starting point and
so the delta between any two releases can be reproduced later:

```powershell
git fetch origin
git log origin/ncat-baseline -1 --oneline

git tag -a "ncat-v$New" origin/ncat-baseline -m "Raw NetCoreApplicationTemplate $New scaffold"
git push origin "ncat-v$New"

git diff --stat "ncat-v$Old" "ncat-v$New"
```

Confirm that the `log` line is the squash commit from the pull request before tagging, and that the
final `diff --stat` matches the delta reviewed in step 3.

Then verify the tagged baseline, which now records `$New` through its tag. Run the check again, from
the repository root:

```powershell
./scripts/Test-NcatBaselineReproducibility.ps1
```

Its evidence must name `ncat-v$New` and report no material changes. The scheduled workflow verifies the
same thing every week from then on.

## 5. Overlay it onto the integrated application

Create the second short-lived working branch from `main` and cherry-pick the tagged baseline
commit:

```powershell
git switch --create $IntegrationBranch origin/main
git cherry-pick -x "ncat-v$New"
```

Resolve conflicts with these rules:

- Keep ReferenceApplication-specific and AsiBackbone package entries.
- Keep the walkthrough Step 6 changes to NCAT-derived data access: the AsiBackbone model configuration and sets in
  `ApplicationDbContext`, the accountability-record exclusions (`AccountabilityRecordTypes`) in
  `ApplicationSaveChangesPipeline` and the UTC timestamp convention, and the governance evidence link in
  `HttpContextApplicationAuditContextAccessor`. Regenerate the model snapshot with `dotnet-ef` rather than hand-merging
  it, then confirm that `AccountabilityRecordPersistenceTests` still pass.
- Keep ReferenceApplication's own `VersionPrefix`, `VersionSuffix`, `AssemblyVersion`, `FileVersion`, and
  `RepositoryUrl` in `Directory.Build.props`. The release version is independent of the NCAT version, which the
  `ncat-v<version>` tags record.
- Adopt NCAT's shared dependency versions.
- Adopt the new generated-migration `.editorconfig` rules.
- Do not replace customized application files unless the release delta actually changes them.
- Do not hand-merge lock files. Resolve `Directory.Packages.props` first, then regenerate the lock
  files.

To clear conflicted lock files before regenerating them:

```powershell
git diff --name-only --diff-filter=U -- '*packages.lock.json' |
  ForEach-Object { git checkout --ours -- $_ }

dotnet restore ReferenceApplication.slnx --force-evaluate
```

During a cherry-pick, `--ours` is the `main` side; the forced restore then rewrites the lock files
from the resolved package versions. Stage the reconciled files and continue:

```powershell
git add --all
git cherry-pick --continue
```

Run the complete validation sequence:

```powershell
dotnet restore ReferenceApplication.slnx --force-evaluate
dotnet restore ReferenceApplication.slnx --locked-mode
dotnet build ReferenceApplication.slnx --configuration Release --no-restore
dotnet format ReferenceApplication.slnx --verify-no-changes --no-restore
dotnet test --solution ReferenceApplication.slnx --configuration Release --no-build
```

The first restore may change lock files even when the cherry-pick had no conflicts; commit any
such change on the pull request branch.

Update every place that records the current NCAT version, at least:

- `README.md` ("The application was generated from NCAT ...")
- [Step 1: Scaffold from NCAT](01-scaffold-from-ncat.md)

Review the resulting diff as an integration change, commit any deliberate conflict resolutions,
and open a separate pull request targeting `main`. Keep the baseline and integration reviews
separate so reviewers can distinguish the upstream NCAT delta from ReferenceApplication's choices.

### Why cherry-pick rather than merge

Merging `ncat-baseline` into `main` would give Git a true merge base between the two branches, so
it could report which upstream changes are already integrated and resolve later refreshes with
full three-way context. Cherry-picking instead keeps `main`'s history linear and makes each refresh
a single reviewable change. The `-x` trailer and the `ncat-v<version>` tags provide the
traceability that a merge would otherwise supply: `git diff ncat-v<old> ncat-v<new>` always
reproduces exactly what a refresh brought in.

## 6. Clean up

After both pull requests are merged, return to the repository root and remove the refresh
worktree, its local branch, and the scratch folder:

```powershell
git worktree remove "..\ReferenceApplication-ncat-$New"
git branch -D $BaselineBranch
Remove-Item -Recurse -Force $Scratch
```

`git branch -D` is required because a squash merge does not make the local branch an ancestor of
`ncat-baseline`. If `git worktree remove` refuses because of untracked files, confirm nothing in
the worktree is still needed and rerun it with `--force`.
