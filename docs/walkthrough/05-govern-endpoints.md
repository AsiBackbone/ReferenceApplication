---
title: "Step 5: Govern Endpoints"
description: How consequential Reference Application endpoints are planned to be routed through AsiBackbone governance before they execute.
---

# Step 5: Govern Endpoints

**Status:** Planned. This page will be updated with the governed endpoints and commit when the step lands.

## Planned outcome

Consequential endpoints will evaluate the sample governance rules before executing, and denials will map to the NCAT Problem Details error responses.

## Planned procedure

1. Identify the consequential endpoints. Read-only and diagnostic endpoints stay ungoverned, and that choice is documented.
2. Attach governance metadata to each consequential endpoint.
3. Map governance outcomes to HTTP results that match the NCAT error-handling conventions.
4. Add integration tests through the NCAT test host for the allow, deny, and acknowledgment-required paths.

## Authoritative references

- [AsiBackbone ASP.NET Core Endpoint Governance](https://asibackbone.github.io/AsiBackbone/articles/aspnetcore-endpoint-governance.html)
- [AsiBackbone Host-Owned Execution Enforcement](https://asibackbone.github.io/AsiBackbone/articles/host-owned-execution-enforcement.html)
- [NCAT Error Handling](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/error-handling.html)
- [NCAT Authorization](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/authorization.html)

## Next

[Step 6: Persist and verify audit evidence](06-persist-and-verify-audit.md)
