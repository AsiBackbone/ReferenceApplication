---
title: "NCAT Baseline and AsiBackbone Integration"
description: API reference and completed step-by-step record of integrating AsiBackbone into an NCAT-generated ASP.NET Core host.
_disableAffix: true
_disableBreadcrumb: true
_disableToc: true
---

# AsiBackbone Reference Application Documentation

<div class="home-hero">
  <p class="home-kicker">NCAT baseline with observable, measurable AsiBackbone governance</p>
  <p class="home-summary">The Reference Application is an ASP.NET Core host generated from the .NET Core Application Template (NCAT). This site records, step by step, how AsiBackbone governed decisions, runnable sample governance rules, persisted audit evidence, an operational evidence explorer, and host-overhead benchmarks were added to it.</p>
  <p class="home-why">The Reference Application exists so teams can follow the exact procedure, in order, that takes a freshly generated NCAT application to a governed AsiBackbone host, and inspect each resulting code change.</p>
  <div class="home-actions" role="group" aria-label="Start with the Reference Application">
    <a class="btn btn-primary" href="walkthrough/index.md">Follow the Walkthrough</a>
    <a class="btn btn-outline-primary" href="api/index.md">API Reference</a>
    <a class="btn btn-outline-primary" href="#is-the-reference-application-a-fit">Is It a Fit?</a>
  </div>
  <p class="home-boundary">This site covers two things only: the generated API reference for the Reference Application and a walkthrough of the procedure used to combine NCAT and AsiBackbone. It is not a training site. <a href="https://asibackbone.github.io/Learning/">AsiBackbone Learning</a> remains the authority for architecture education, and the NCAT and AsiBackbone sites remain authoritative for their own behavior.</p>
</div>

## Start here

<div class="entry-point-grid">
  <a class="entry-point" href="walkthrough/index.md">
    <strong>Walk through the integration</strong>
    <span>The completed procedure from <code>dotnet new netcoreapp-template</code> to an observable and measurable governed host.</span>
  </a>
  <a class="entry-point" href="walkthrough/01-scaffold-from-ncat.md">
    <strong>See the NCAT baseline</strong>
    <span>How the application was generated and how the untouched baseline is preserved for comparison.</span>
  </a>
  <a class="entry-point" href="walkthrough/04-define-governance-rules.md">
    <strong>Read the sample governance rules</strong>
    <span>The sample rules this host evaluates, their stable reason codes, and how they are tested.</span>
  </a>
  <a class="entry-point" href="walkthrough/06-persist-and-verify-audit.md">
    <strong>Trace a governed change</strong>
    <span>How a change is linked to the decision that permitted it through the audit ledger, NCAT mutation audit, and audit completion.</span>
  </a>
  <a class="entry-point" href="walkthrough/07-observe-governance.md">
    <strong>Observe governance in action</strong>
    <span>Use the Development explorer to run governed endpoints and inspect their persisted evidence.</span>
  </a>
  <a class="entry-point" href="walkthrough/07-observe-governance.md#governance-sandbox">
    <strong>Experiment in the governance sandbox</strong>
    <span>A Development-only learning aid, not a production feature: simulate and replay decisions, follow the outbox, and trigger failure paths.</span>
  </a>
  <a class="entry-point" href="walkthrough/08-benchmarks.md">
    <strong>Measure host overhead</strong>
    <span>Run the repeatable BenchmarkDotNet scenarios and understand their measurement boundary.</span>
  </a>
  <a class="entry-point" href="api/index.md">
    <strong>Browse the API reference</strong>
    <span>Generated type and member pages for the Reference Application's production projects.</span>
  </a>
</div>

## Is the Reference Application a fit?

<div class="fit-grid">
  <section class="fit-panel" aria-labelledby="fit-use">
    <h3 id="fit-use">Use this site when</h3>
    <ul>
      <li>you already chose NCAT and AsiBackbone and want to follow their integration in one real host;</li>
      <li>you want the integration steps in order, each tied to the code it produced; and</li>
      <li>you want to follow the implementation of sample governance rules and later compare the completed examples against your own.</li>
    </ul>
  </section>
  <section class="fit-panel" aria-labelledby="fit-simpler">
    <h3 id="fit-simpler">Look elsewhere when</h3>
    <ul>
      <li>you are deciding <em>whether</em> to adopt governance: start with <a href="https://asibackbone.github.io/Learning/architecture/when-aspnet-core-authorization-is-enough.html">When ASP.NET Core Authorization Is Enough</a> and <a href="https://asibackbone.github.io/Learning/architecture/when-a-simple-application-service-is-enough.html">When a Simple Application Service Is Enough</a>;</li>
      <li>you need to learn the concepts: use <a href="https://asibackbone.github.io/Learning/">AsiBackbone Learning</a>; or</li>
      <li>you need the authoritative contract for a framework or template feature: use the <a href="https://asibackbone.github.io/AsiBackbone/">AsiBackbone</a> or <a href="https://asibackbone.github.io/NetCoreApplicationTemplate/">NCAT</a> documentation.</li>
    </ul>
    <p>The Reference Application is an example, not a product. Its sample rules illustrate patterns and are not recommendations for any specific domain.</p>
  </section>
</div>

## At a glance

| Area | Current reference |
|:---|:---|
| Release line | Stable `1.0.0` (`1.x` compatibility contract) |
| Supported platform | .NET 10.0 |
| Host baseline | [NCAT](https://asibackbone.github.io/NetCoreApplicationTemplate/) `2.11.7`, generated with `dotnet new netcoreapp-template` (default cookie authentication, SQLite) |
| Governance framework | [AsiBackbone](https://asibackbone.github.io/AsiBackbone/) `7.5.2`, integrated through walkthrough Step 8 |
| Governance sandbox | Development-only explorer tools for experimenting with the pipeline; never registered in Staging or Production |
| Untouched scaffold | [`ncat-baseline` branch](https://github.com/AsiBackbone/ReferenceApplication/tree/ncat-baseline) |
| Source | [AsiBackbone/ReferenceApplication](https://github.com/AsiBackbone/ReferenceApplication) |

## Related projects

The Reference Application consumes both related projects; neither of them depends on the Reference Application.

<div class="project-grid">
  <article class="project-card">
    <span class="project-role">Learn the patterns</span>
    <h3><a href="https://asibackbone.github.io/Learning/">AsiBackbone Learning</a></h3>
    <p>Practical .NET architecture tutorials, labs, runnable samples, and reference patterns for governed execution, secure applications, AI integration, and policy-driven systems, including comparisons that show when these patterns are unnecessary.</p>
    <p class="project-links">
      <a href="https://asibackbone.github.io/Learning/" aria-label="AsiBackbone Learning documentation">Documentation</a>
      <a href="https://github.com/AsiBackbone/Learning" aria-label="AsiBackbone Learning repository">Repository</a>
      <a href="https://doi.org/10.5281/zenodo.21938556" aria-label="AsiBackbone Learning archive (DOI)">Archive (DOI)</a>
    </p>
  </article>
  <article class="project-card">
    <span class="project-role">Add governed decisions</span>
    <h3><a href="https://asibackbone.github.io/AsiBackbone/">AsiBackbone</a></h3>
    <p>A .NET governance and policy-control framework for auditable decision flows: policy evaluation, auditable decision records, acknowledgment workflows, capability-based authorization, host-owned execution boundaries, and ASP.NET Core integration.</p>
    <p class="project-links">
      <a href="https://asibackbone.github.io/AsiBackbone/" aria-label="AsiBackbone documentation">Documentation</a>
      <a href="https://github.com/AsiBackbone/AsiBackbone" aria-label="AsiBackbone repository">Repository</a>
      <a href="https://doi.org/10.5281/zenodo.20546032" aria-label="AsiBackbone archive (DOI)">Archive (DOI)</a>
    </p>
  </article>
  <article class="project-card">
    <span class="project-role">Start a secure application</span>
    <h3><a href="https://asibackbone.github.io/NetCoreApplicationTemplate/">.NET Core Application Template</a></h3>
    <p>A secure-by-default ASP.NET Core application template with authentication and authorization, structured logging, security headers, rate limiting, EF Core data access, reverse proxy support, and Problem Details error handling.</p>
    <p class="project-links">
      <a href="https://asibackbone.github.io/NetCoreApplicationTemplate/" aria-label=".NET Core Application Template documentation">Documentation</a>
      <a href="https://github.com/AsiBackbone/NetCoreApplicationTemplate" aria-label=".NET Core Application Template repository">Repository</a>
      <a href="https://doi.org/10.5281/zenodo.20373042" aria-label=".NET Core Application Template archive (DOI)">Archive (DOI)</a>
    </p>
  </article>
  <article class="project-card" aria-current="page">
    <span class="project-role">Follow the integration</span>
    <h3>AsiBackbone Reference Application <span class="current-site">Current site</span></h3>
    <p>An NCAT-generated ASP.NET Core host with observable governance, persisted audit evidence, host-overhead benchmarks, a completed step-by-step implementation record, and a generated API reference.</p>
    <p class="project-links">
      <a href="https://github.com/AsiBackbone/ReferenceApplication" aria-label="AsiBackbone Reference Application repository">Repository</a>
    </p>
  </article>
</div>

## Design philosophy

<div class="philosophy-callout">
  <p class="philosophy-statement">We favor architectures where consequential operations are explicit, constrained, reviewable, and auditable.</p>
  <p>We also document when these patterns are unnecessary: see <a href="#is-the-reference-application-a-fit">Look elsewhere when</a>, <a href="https://asibackbone.github.io/Learning/architecture/when-aspnet-core-authorization-is-enough.html">When ASP.NET Core Authorization Is Enough</a>, and <a href="https://asibackbone.github.io/Learning/architecture/when-a-simple-application-service-is-enough.html">When a Simple Application Service Is Enough</a>.</p>
  <p>The projects focus on practical .NET engineering. They do not claim regulatory certification or automatic compliance.</p>
</div>
