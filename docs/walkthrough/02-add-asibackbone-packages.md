---
title: "Step 2: Add AsiBackbone Packages"
description: The AsiBackbone 7.1.1 package references added under central package management with locked dependency graphs.
---

# Step 2: Add AsiBackbone Packages

**Status:** Complete on `initial_development` with AsiBackbone `7.1.1`.

## Outcome

The host projects reference only the AsiBackbone packages needed by the planned integration. All five packages use the stable `7.1.1` version through central package management, and each project's resolved dependency graph is recorded in its existing `packages.lock.json` file.

No AsiBackbone services or middleware are registered in this step. That wiring remains Step 3, keeping package acquisition separate from runtime behavior changes.

## Package placement

| Package | Referenced by | Purpose |
|:---|:---|:---|
| `AsiBackbone.Core` | Web, Infrastructure | Governance primitives, policy evaluation, and decision receipts |
| `AsiBackbone.AspNetCore` | Web | Endpoint governance and HTTP integration |
| `AsiBackbone.EntityFrameworkCore` | Infrastructure | Model configuration and stores for the host-owned `DbContext` |
| `AsiBackbone.Analyzers` | Web, Infrastructure | Build-time safety rails; marked `PrivateAssets="all"` so it remains a build concern |
| `AsiBackbone.Testing` | Tests | Test harness helpers for later governance assertions |

`AsiBackbone.AspNetCore`, `AsiBackbone.EntityFrameworkCore`, and `AsiBackbone.Testing` already depend on Core where required. The Web and Infrastructure projects nevertheless reference `AsiBackbone.Core` directly because later walkthrough steps will use its public contracts directly; this avoids relying on transitive compile assets for code the host owns.

## Procedure

1. Confirm `7.1.1` as the current stable release in [AsiBackbone Getting Started](https://asibackbone.github.io/AsiBackbone/articles/getting-started.html) and the [AsiBackbone 7.1.1 release](https://github.com/AsiBackbone/AsiBackbone/releases/tag/v7.1.1).

2. Add each version once to `Directory.Packages.props`, then add version-less `PackageReference` items to the projects listed above.

3. Regenerate the lock files after the intentional dependency change:

   ```powershell
   dotnet restore ReferenceApplication.slnx --force-evaluate
   ```

4. Validate the resulting graph and confirm the analyzer package raises no findings on the NCAT-derived baseline:

   ```powershell
   dotnet restore ReferenceApplication.slnx --locked-mode
   dotnet build ReferenceApplication.slnx --configuration Release --no-restore
   dotnet test ReferenceApplication.slnx --configuration Release --no-build --no-restore
   ```

The repository treats compiler and analyzer warnings as errors, so a successful Release build is also the Step 2 analyzer compatibility check.

## Authoritative references

- [AsiBackbone Getting Started](https://asibackbone.github.io/AsiBackbone/articles/getting-started.html)
- [AsiBackbone NetCoreApplicationTemplate Host Validation](https://asibackbone.github.io/AsiBackbone/articles/netcoreapplicationtemplate-host-validation.html)
- [NCAT Dependency Update Policy](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/dependency-update-policy.html)

## Next

[Step 3: Register AsiBackbone services](03-register-asibackbone-services.md)
