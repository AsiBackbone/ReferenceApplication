---
title: "Step 3: Register AsiBackbone Services"
description: The AsiBackbone service and middleware composition in the NCAT startup path with host-owned actor mapping.
---

# Step 3: Register AsiBackbone Services

**Status:** Complete with AsiBackbone `7.2.0`. Released in [`v0.1.0-alpha.3`](https://github.com/AsiBackbone/ReferenceApplication/releases/tag/v0.1.0-alpha.3).

## Outcome

The host registers AsiBackbone's ASP.NET Core integration services through an application-owned extension and adds
endpoint governance to the established NCAT middleware pipeline. This step adds no governance rules or governed
endpoints; those are [Step 4](04-define-governance-rules.md) and [Step 5](05-govern-endpoints.md).

The integration preserves host ownership of authentication. NCAT continues to authenticate users and normalize
provider-specific claims. AsiBackbone maps the normalized `application:subject` claim to the stable governance actor
ID and `application:name` to its display name. Standard stable identifier and display-name claims remain fallbacks when
a host deliberately disables or customizes NCAT claim transformation.

## Service registration

`AddApplicationAsiBackbone` lives with the other host registration extensions under
`src/ReferenceApplication.Web/Extensions`. It calls `AddAsiBackboneAspNetCore()` and configures
`HttpGovernanceActorContextOptions` so the application-owned claims take precedence:

```csharp
builder.Services.AddApplicationAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddApplicationAuthorization(builder.Configuration);
builder.Services.AddApplicationDataAccess(builder.Configuration);
builder.Services.AddApplicationAsiBackbone();
```

The framework registration supplies the HTTP actor resolver, request-correlation resolver, acknowledgment challenge
service, endpoint governance service, and validated integration options. Persistence, policy evaluators, capability
validators, and decision receipt sinks are intentionally not selected in this step because the host does not define
those boundaries until later steps.

## Middleware placement

The NCAT-derived pipeline calls the optional endpoint-governance middleware after routing, authentication, and
authorization, and before controller and Razor Page endpoints execute:

```csharp
app.UseRouting();
// Rate limiting, authentication, and authorization remain in their documented order.
app.UseAsiBackboneEndpointGovernance(requireEndpointRoutingRegistered: true);
app.MapControllers();
app.MapRazorPages();
```

Passing `requireEndpointRoutingRegistered: true` converts an accidental future move ahead of `UseRouting()` into a
startup failure. AsiBackbone endpoint governance remains opt-in at this stage, so existing endpoints without governance
metadata continue through the pipeline unchanged.

## Verification

The Step 3 tests verify that:

- registration returns the same service collection and resolves the framework's scoped integration services;
- normalized NCAT subject and name claims resolve to a known, authenticated human governance actor; and
- the complete web host starts with the middleware in place and exposes the expected AsiBackbone services.

The complete inherited NCAT suite runs alongside those tests:

```powershell
dotnet restore ReferenceApplication.slnx --locked-mode
dotnet build ReferenceApplication.slnx --configuration Release --no-restore
dotnet test ReferenceApplication.slnx --configuration Release --no-build --no-restore
```

## Authoritative references

- [AsiBackbone ASP.NET Core Integration Boundary](https://asibackbone.github.io/AsiBackbone/articles/aspnetcore-integration-boundary.html)
- [AsiBackbone ASP.NET Core Endpoint Governance](https://asibackbone.github.io/AsiBackbone/articles/aspnetcore-endpoint-governance.html)
- [NCAT Middleware Pipeline](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/middleware.html)
- [NCAT Authentication](https://asibackbone.github.io/NetCoreApplicationTemplate/articles/authentication.html)

## Local setup checkpoint

Before continuing with database-dependent integration scenarios, apply the inherited NCAT migration history by following
[Create the development database](local-database-setup.md). Database creation is explicit because the host does not
automatically create or migrate its database at startup.

## Next

[Step 4: Define governance rules](04-define-governance-rules.md)
