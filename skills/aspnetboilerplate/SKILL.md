---
name: aspnetboilerplate
description: Analyze ASP.NET Boilerplate (Abp.*) source projects and extend AbpToVolo conversion rules. Use for legacy AbpModule lifecycle, Module Zero, AbpSession, repositories, unit of work, authorization, localization, and preserving source behavior before migration to Volo.Abp.
---

# ASP.NET Boilerplate source analysis

## Establish the source contract

1. Read the source `.sln`/`.slnx`, project files, module classes, DbContext, and startup configuration. Record exact package versions; distinguish ASP.NET Boilerplate (`Abp.*`) from ABP Framework (`Volo.Abp.*`). Do not infer versions from product names.
2. Identify Core, Application, EntityFrameworkCore, Web.Core, Web.Host, Migrator, and test layers. Record custom layer names and framework integrations.
3. Inventory Module Zero users, roles, tenants, organization units, custom members, key types, indexes, foreign keys, and database-specific SQL. Preserve domain entity keys unless a conversion rule explicitly requires a change.
4. Capture observable behavior before rewriting: routes and verbs, JSON envelopes, DTO fields, permission names, validation failures, tenant context, audit values, transaction boundaries, and background job behavior.
5. Inspect `PreInitialize`, `Initialize`, `PostInitialize`, and shutdown logic separately. Preserve dependency order and distinguish configuration, registrations, and resolving/running services. Do not convert lifecycle methods by renaming alone.

## Work in this converter

- Start from [the pipeline and development process](../../docs/development-process.fa.md).
- Find existing rules in `src/AbpToVolo.Engine/Mappings`, `CodeMigration/Rewriters`, and `Steps`. Prefer extending the narrowest existing rule.
- Inspect `CSharpFileMigrator.Pipeline` before changing rewrite order. Use Roslyn nodes and semantic information where available; preserve similarly named application types, comments, strings, and unrelated overloads.
- Track `AbpSession` tenant/user usage and synchronous repository calls through their callers. Preserve null/host semantics and async control flow.
- Inspect unit-of-work attributes, isolation, nested scopes, filters, raw connections, and active transactions. Verify rollback and tenant restoration; compilation alone cannot establish equivalent behavior.
- Treat permission names and localization keys as application contracts. Move definitions without silently granting permissions or dropping translations.
- Keep unsupported business logic in the report and preserved legacy code. Do not declare a conversion complete because code was moved out of compilation or replaced by a stub.

## Verify a rule

1. Add a minimal failing source example and expected behavior to the closest existing test class.
2. Include a negative case with a similarly named non-framework API and a repeat-conversion/idempotence case where applicable.
3. Run the converter tests. For EF/SQL changes, also run integration tests against the actual database provider on a disposable database copy.
4. Compare original and converted endpoint/permission/tenant behavior for the affected feature.
5. Record unsupported patterns and unresolved choices in [remaining work](../../docs/remaining-work-and-decisions.fa.md).

## Official references

Read documentation matching the source package version before relying on an API signature. These are navigation links, not a version lock:

- [Module system](https://aspnetboilerplate.com/Pages/Documents/Module-System): lifecycle responsibilities and dependency ordering.
- [Unit of work](https://aspnetboilerplate.com/Pages/Documents/Unit-Of-Work): connection, transaction, and nested scope behavior.
- [Framework source](https://github.com/aspnetboilerplate/aspnetboilerplate): inspect the tag matching the source packages for ambiguous APIs.

These repository instructions do not install a personal ChatGPT skill and do not themselves run a conversion.
