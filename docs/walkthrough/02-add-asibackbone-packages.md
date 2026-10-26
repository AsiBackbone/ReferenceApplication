---
title: "Step 2: Add AsiBackbone Packages"
description: The historical and current AsiBackbone package references under central package management with locked dependency graphs.
---

# Step 2: Add AsiBackbone Packages

**Status:** Historical milestone complete with AsiBackbone `7.2.1`. First released in [`v0.1.0-alpha.2`](https://github.com/AsiBackbone/ReferenceApplication/releases/tag/v0.1.0-alpha.2) on AsiBackbone `7.2.0`; the original five packages were later updated to the `7.2.1` patch release, which [Step 5](05-govern-endpoints.md#capability-before-acknowledgment) requires. The repository has continued to evolve since that milestone; the current package set is documented separately below.

## Outcome

Step 2 originally established five direct AsiBackbone package references under central package management: `Analyzers`, `AspNetCore`, `Core`, `EntityFrameworkCore`, and `Testing`. They shipped first at `7.2.0` and then moved to `7.2.1` without changing the package placement described by the original step.

Later work added signed capability-token support and governance telemetry, so the current repository references more AsiBackbone packages than the original Step 2 milestone. Every AsiBackbone package version is still pinned centrally, and each project's resolved dependency graph remains recorded in its checked-in `packages.lock.json` file.

This step adds packages only; it registers no AsiBackbone services or middleware. That wiring is [Step 3](03-register-asibackbone-services.md), so package acquisition stays separate from runtime behavior changes.

## Historical package placement

The original Step 2 package placement was:

| Package | Referenced by | Purpose |
|:---|:---|:---|
| `AsiBackbone.Core` | Web, Infrastructure | Governance primitives, policy evaluation, and decision receipts |
| `AsiBackbone.AspNetCore` | Web | Endpoint governance and HTTP integration |
| `AsiBackbone.EntityFrameworkCore` | Infrastructure | Model configuration and stores for the host-owned `DbContext` |
| `AsiBackbone.Analyzers` | Web, Infrastructure | Build-time safety rails; marked `PrivateAssets="all"` so it remains a build concern |
| `AsiBackbone.Testing` | Tests | Test harness helpers for later governance assertions |

`AsiBackbone.AspNetCore`, `AsiBackbone.EntityFrameworkCore`, and `AsiBackbone.Testing` already depend on Core where required. The Web and Infrastructure projects nevertheless reference `AsiBackbone.Core` directly because later walkthrough steps use its public contracts directly; this avoids relying on transitive compile assets for code the host owns.

## Current package set — AsiBackbone `7.5.2`

The current repository pins nine AsiBackbone packages at `7.5.2`. Eight are direct project references. `AsiBackbone.Storage.InMemory` is centrally pinned because it remains in the test dependency graph transitively; no project references it directly.

| Package | Referenced by | Why it is present |
|:---|:---|:---|
| `AsiBackbone.Analyzers` | Web, Infrastructure | Original Step 2 build-time safety rails |
| `AsiBackbone.AspNetCore` | Web | Original Step 2 HTTP governance integration |
| `AsiBackbone.Core` | Web, Infrastructure | Original Step 2 governance contracts used directly by host code |
| `AsiBackbone.EntityFrameworkCore` | Infrastructure | Original Step 2 EF Core stores and model configuration |
| `AsiBackbone.OpenTelemetry` | Web | Added by [#78](https://github.com/AsiBackbone/ReferenceApplication/issues/78) to export governance spans and metrics |
| `AsiBackbone.Signing.LocalDevelopment` | Web | Added during [#58](https://github.com/AsiBackbone/ReferenceApplication/issues/58) for the Development signing path used by capability grants |
| `AsiBackbone.Signing.ManagedKey` | Web | Added during [#58](https://github.com/AsiBackbone/ReferenceApplication/issues/58) for configured-key signing and verification outside the local-development path |
| `AsiBackbone.Storage.InMemory` | Central transitive pin (tests) | Kept in central package management because the test graph resolves it transitively; it is not a direct `PackageReference` |
| `AsiBackbone.Testing` | Tests | Original Step 2 test helpers; the current test graph also brings in the centrally pinned in-memory storage package |

The current section is intentionally separate from the historical status line. Other walkthrough steps can continue to record the AsiBackbone version that completed their milestone without implying that version is still the repository-wide package baseline.

## Procedure

1. Confirm the current version in `Directory.Packages.props` and the matching [AsiBackbone `7.5.0` release](https://github.com/AsiBackbone/AsiBackbone/releases/tag/v7.5.0). Treat the `7.2.0` and `7.2.1` versions above as Step 2 history, not as the current repository baseline.

2. Keep each AsiBackbone version in `Directory.Packages.props`, then use version-less `PackageReference` items in the projects that consume the package directly:

   - `Directory.Packages.props`:

     ```xml
     <ItemGroup>
       <PackageVersion Include="AsiBackbone.Analyzers" Version="7.5.0" />
       <PackageVersion Include="AsiBackbone.AspNetCore" Version="7.5.0" />
       <PackageVersion Include="AsiBackbone.Core" Version="7.5.0" />
       <PackageVersion Include="AsiBackbone.EntityFrameworkCore" Version="7.5.0" />
       <PackageVersion Include="AsiBackbone.OpenTelemetry" Version="7.5.0" />
       <PackageVersion Include="AsiBackbone.Signing.LocalDevelopment" Version="7.5.0" />
       <PackageVersion Include="AsiBackbone.Signing.ManagedKey" Version="7.5.0" />
       <PackageVersion Include="AsiBackbone.Storage.InMemory" Version="7.5.0" />
       <PackageVersion Include="AsiBackbone.Testing" Version="7.5.0" />
     </ItemGroup>
     ```

   - `src/ReferenceApplication.Web/ReferenceApplication.Web.csproj`:

     ```xml
     <ItemGroup>
       <PackageReference Include="AsiBackbone.Analyzers" PrivateAssets="all" />
       <PackageReference Include="AsiBackbone.AspNetCore" />
       <PackageReference Include="AsiBackbone.Core" />
       <PackageReference Include="AsiBackbone.OpenTelemetry" />
       <PackageReference Include="AsiBackbone.Signing.LocalDevelopment" />
       <PackageReference Include="AsiBackbone.Signing.ManagedKey" />
     </ItemGroup>
     ```

   - `src/ReferenceApplication.Infrastructure/ReferenceApplication.Infrastructure.csproj`:

     ```xml
     <ItemGroup>
       <PackageReference Include="AsiBackbone.Analyzers" PrivateAssets="all" />
       <PackageReference Include="AsiBackbone.Core" />
       <PackageReference Include="AsiBackbone.EntityFrameworkCore" />
     </ItemGroup>
     ```

   - `tests/ReferenceApplication.Web.Tests/ReferenceApplication.Web.Tests.csproj`:

     ```xml
     <ItemGroup>
       <PackageReference Include="AsiBackbone.Testing" />
     </ItemGroup>
     ```

   Do not add a direct `AsiBackbone.Storage.InMemory` reference merely because it is centrally pinned; its current role is transitive.

3. Regenerate the lock files after an intentional dependency change:

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
