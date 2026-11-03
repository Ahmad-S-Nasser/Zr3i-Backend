# Zr3i platform

Multi-tenant SaaS for agricultural risk. Two MVP modules: **Verification (R0)** and **Risk (CRI per parcel, FRI per farm, method FRI-2026-01)**. English and Arabic (RTL) everywhere: UI, reports, errors.
Legal entity: DigiWell for Digital Solutions LLC (trading as Zr3i).

## Stack
.NET 10, ASP.NET Core, MongoDB (local single-node replica set with auth), NetTopologySuite + ProjNet, GDAL for raster window reads. One solution, one web app, one worker, one database. Modular monolith: modules are projects with a manifest, not services.

## Repos and scope
Two repos: this one (Zr3i-Backend, .NET API and worker) and Zr3i-Frontend (React/Vite, SEO and AEO friendly, built from the design system). `Zr3i.Web` is API only, no Razor/Blazor UI; the plan's UI tasks (W5-x) are built in the Frontend repo against this API. AI services are not built here: an external AI team owns them, so integrate through a narrow interface only. Local MongoDB runs without auth for development (ADR 0001).

## Layout
src/Zr3i.Kernel (tenancy, users, roles, grants, audit, jobs, custom fields, module registry)
src/Zr3i.Geo (geometry, pixel test, zonal stats, COG window reads)
src/Zr3i.Modules.Verification (R0 rules, findings, evidence, accuracy register)
src/Zr3i.Modules.Risk (CRI, FRI, bands, indicator results)
src/Zr3i.Web (API + UI, EN/AR, RTL) · src/Zr3i.Worker (job runner) · src/Zr3i.Reports (HTML to PDF, CSV, SHA-256 manifest)
tests/ (unit, golden datasets, two-tenant isolation, contract)
docs/ (plan, architecture, methodology, design system, ADRs)

## Hard rules (never break; if a task seems to need it, stop and ask)
1. Every collection has `tenantId`. Repositories take a `TenantScope` and add the filter. No code touches the Mongo driver outside repositories. The two-tenant isolation test must stay green.
2. Core code never branches on account type. Account types (Zr3i admin, Bank, Insurance, Company, Farmer) are data presets: default roles, nav, modules, home view, sign-up fields.
3. Farmer owns consent. Any grant to another tenant has a purpose, a period and a Withdraw action. Every cross-tenant read is audit-logged.
4. Indicator results are immutable: method version, input hash, value, components, missing components, maturity (specified | calibrated | validated). Recompute creates a new result.
5. Append-only collections (audit, consent, verification, evidence, indicator results): the app role has find and insert only.
6. Nothing heavy runs in a web request. Use the Mongo job queue (per-tenant fairness). Bulk import is idempotent by externalId.
7. Every string is in a resource file with English and Arabic. No hardcoded UI text. Use logical CSS properties (start/end), never left/right.
8. Secrets never in the repo. Lock files committed. Pre-commit secret scan.

## Domain rules
- Verification: states the fact, a person decides. No approve/reject/recommend anywhere. Every finding has a status and `accuracy_ref`. "Below resolution" is never shown as "no crop". Use NOT_ASSESSABLE when evidence is insufficient.
- Risk: implement exactly FRI-2026-01 (the local methodology (not in git)). Do not tune weights or cut points. Component weights 25/20/15/15/10/8/7; a component without data is "not established" (stored with its weight, never zero) and the weight is renormalised. FRI = area-weighted mean of parcel CRIs plus 5 adjustments. Bands A to E by percentile cuts 0/20/40/60/80, band is a pure function of percentile.
- Every CRI/FRI value, screen, export and report shows "Specified · not calibrated" and the method version. Never call it a credit score, probability of default, approval or recommendation. No ranking page. Band colours are never verdict colours.
- Worked examples must reproduce: parcel ZR-KS-03 CRI 50.3; Delta Agro Estate AWCRI 38.7, adjustments +1.7, FRI 40.4. Spec gap A-2 (40.4 vs the band): follow the percentile rule and record it in an ADR.
- Area computed in EPSG:32636.

## UI
Build from the design system tokens (the design system (kept locally)). Deep field green #17472D is the brand colour; corners are soft (6/10/14 px). Match the design canvas boards. Never invent new colours or components without asking.

## How to work
- Work in the order of the local plan (not in git) (week task ids W1-1 …). One task id per branch, small commits, each item behind its test.
- Write the failing test first for domain logic. Run `dotnet build` and `dotnet test` before saying a task is done.
- Plan mode first for anything touching tenancy, consent, audit or indicators.
- Record decisions in docs/adr/NNNN-title.md (local, not in git).
- Ask before adding a NuGet package; prefer the BCL and the listed libraries.
