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

The commands below use the `2.11.4` to `2.11.5` refresh as a concrete example. Every version
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

## Prerequisites

- A clean clone of ReferenceApplication with `origin` fetched, including tags.
- The .NET SDK required by `global.json`.
- Windows PowerShell 5.1 or PowerShell 7. The commands use `robocopy`, which ships with Windows.
- The current baseline commit is tagged `ncat-v<version>`. The initial scaffold is tagged
  `ncat-v2.11.4`.

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
explicitly, so the delta contains only the release change. Add the option to Step 1 so later
refreshes generate with the same inputs.

### Check that generation is deterministic

```powershell
git diff --no-index --stat -- new new-repeat
```

This must print nothing (exit code `0`). Any difference means the template generates
nondeterministic content, such as GUIDs, random ports, or timestamps. That content would appear
as noise in every refresh. Stop and decide how to handle those files before continuing.

## 2. Prepare the baseline worktree

From the local ReferenceApplication repository root, confirm that the remote baseline is still
exactly the tagged `$Old` scaffold, then create a worktree for the refresh:

```powershell
git fetch origin --tags

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
`git rev-list -n 1` resolves the annotated tag to the commit it points at.

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

Open a pull request targeting `ncat-baseline`, not `main`.

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

## 5. Overlay it onto the integrated application

Create the second short-lived working branch from `main` and cherry-pick the tagged baseline
commit:

```powershell
git switch --create $IntegrationBranch origin/main
git cherry-pick -x "ncat-v$New"
```

Resolve conflicts with these rules:

- Keep ReferenceApplication-specific and AsiBackbone package entries.
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
