---
title: Development-only demo identity controls
description: Safely select the fixed role and capability claims used to exercise the governed endpoint examples.
---

# Development-only demo identity controls

The governance explorer needs to demonstrate allowed, denied, and acknowledgment-required decisions without requiring
an external identity provider. The Reference Application therefore includes an optional local demo identity control.
It is a walkthrough aid, not an authentication provider and not a supported deployment feature.

## Safety boundary

The checked-in reference configuration enables the control for local Development runs. Its service, sign-in endpoint,
disposable database, and reset endpoint are registered only when both conditions are true:

1. the host environment is exactly `Development`; and
2. `ReferenceApplication:DemoIdentity:Enabled` is explicitly set to `true`.

Setting the switch in `Staging`, `Production`, or another environment does not register the service or endpoint. The
application also writes a prominent startup warning whenever the control is active.

## Configure it locally

The checked-in setting is suitable for a local walkthrough. Override it with .NET user secrets when you want to turn
the demonstration off without changing the file:

```powershell
dotnet user-secrets set "ReferenceApplication:DemoIdentity:Enabled" false `
  --project src/ReferenceApplication.Web
```

Remove that override or set it to `true`, then run the application, to use the demonstration controls.

Open `/Account/Login`. The development panel can add either or both of these fixed claims to a temporary application
cookie:

- the first configured administrator role; and
- `referenceapplication.consequential.execute`.

It never accepts arbitrary claim types or values. Clear the demo identity with the normal sign-out action, or remove
the user-secret switch to remove the control on the next startup.

The active demonstration uses `ConnectionStrings:DemonstrationDatabase`, a separate SQLite file. Open the governance
explorer as the demo administrator and select **Reset demonstration database** to delete that disposable file and
reapply the migrations. The reset route is not registered outside Development or when the demo switch is false. The
normal application database has no evidence-deletion path.

## Exercise the real governance pipeline

The demo form itself requires an antiforgery token. After it creates the cookie, requests use the same authentication,
authorization, antiforgery, and AsiBackbone endpoint-governance pipeline as any other signed-in user. Only the local
source of the two selectable claims differs.

| Selected claims | Example outcome |
|:---|:---|
| Neither | Administrator and capability operations are denied. |
| Administrator only | The administrator operation is allowed; capability operations are denied. |
| Capability only | The capability operation is allowed; administrator operations are denied. |
| Administrator and capability | The consequential operation reaches its acknowledgment-required outcome. |

Do not expose a host with this control active to untrusted users or networks. Use a real identity provider and
authoritative role and permission assignments for every shared or deployed environment.
